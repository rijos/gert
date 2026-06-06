"""run.py — the Gert E2E launcher (testing.md §9).

In order:

1. **Boot the mock upstreams** (vLLM + SearXNG) on localhost, then **boot the
   host** with ``dotnet run --launch-profile FakeE2E`` (whose config points the
   real ``Gert.External`` clients at the mock URLs). Or attach to an already-
   running pair with ``--base-url``. Wait for ``/healthz``.
2. **Mint tokens** in-process via :mod:`tools.smoke.tokens` (no HTTP round-trip).
3. **Inject + drive** — for each ``(browser, role)`` in the matrix, seed the token
   and run the scenarios.
4. **Report** pass/fail per scenario; trace + screenshot on failure under
   ``tools/smoke/artifacts/``.

Token injection note: ``services/auth.js`` keeps the access token in an in-memory
module variable (security F2) — it is NEVER read from localStorage. So the
launcher injects ``window.GERT_DEV_TOKEN`` via a Playwright **init script** (runs
before any app module), and a dev-only branch in ``ensureSession`` consumes it.
This is gated by the presence of the injected global, which production never sets.

Flags: ``--browser``, ``--role``, ``--headed``, ``--keep-open``, ``--base-url``,
``--vllm-url``/``--vllm-model`` (point chat at a REAL vLLM), ``--searxng-url``
(point web search at a REAL SearXNG); whatever isn't pointed at a real upstream
stays mocked — see ``make serve-mock-vllm``.

This launcher needs browsers installed (``uv run playwright install chromium
firefox``) — only the CI/staging web job has them. The non-browser parts (token
mint, specs conformance, mocks boot, ``--api-smoke``) run without browsers; see
the README.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import NamedTuple

import uvicorn
from playwright.sync_api import ConsoleMessage, Error
from starlette.applications import Starlette

from . import tokens
from .mocks import SEARXNG_PORT, VLLM_PORT
from .mocks.searxng import app as searxng_app
from .mocks.vllm import app as vllm_app
from .pages import AppPage
from .proxy import make_proxy_app

# A matrix result row: (browser, role, scenario, ok, detail).
type MatrixResult = tuple[str, str, str, bool, str]

REPO_ROOT = Path(__file__).resolve().parents[2]
ARTIFACTS_DIR = Path(__file__).resolve().parent / "artifacts"
# The FakeE2E host's user-data root (Storage__DataRoot, resolved against src/Gert.Api/).
# Wiped on every harness-owned boot — NOT the sibling .dev/jwt keypair, which is
# meant to be reused across runs.
DATA_ROOT = REPO_ROOT / "src" / "Gert.Api" / ".dev" / "e2e-data"

DEFAULT_HOST = "http://127.0.0.1:5217"
BROWSERS = ["chromium", "firefox"]
# The full click-through runs admin+user; `limited` is added only by the RBAC
# scenario (it is about the entitlement ceiling).
MATRIX_ROLES = ["admin", "user"]


# --- the init script that seeds the in-memory token --------------------------
def _init_script(token: str) -> str:
    # Runs before app.js. ensureSession() reads window.GERT_DEV_TOKEN in its
    # dev branch and installs it as the in-memory bearer.
    return f"window.GERT_DEV_TOKEN = {token!r};"


# --- mock upstreams (in-process uvicorn servers on background threads) --------
class _ServerThread:
    def __init__(self, app: Starlette, port: int) -> None:
        config = uvicorn.Config(app, host="127.0.0.1", port=port, log_level="warning")
        self.server = uvicorn.Server(config)
        self.thread = threading.Thread(target=self.server.run, daemon=True)

    def start(self) -> None:
        self.thread.start()

    def stop(self) -> None:
        self.server.should_exit = True


def _boot_mocks(
    *, include_vllm: bool = True, include_searxng: bool = True
) -> list[_ServerThread]:
    servers: list[_ServerThread] = []
    if include_vllm:
        servers.append(_ServerThread(vllm_app, VLLM_PORT))
    if include_searxng:
        servers.append(_ServerThread(searxng_app, SEARXNG_PORT))
    for s in servers:
        s.start()
    return servers


# --- the host ----------------------------------------------------------------
def _boot_host(extra_config: list[str] | None = None) -> subprocess.Popen[bytes]:
    # Trailing `-- --Key=value` args reach the host's command-line configuration
    # provider, which wins over the FakeE2E launch-profile env vars — that is how
    # --vllm-url redirects the real adapters without touching launchSettings.json.
    return subprocess.Popen(
        [
            "dotnet",
            "run",
            "--project",
            str(REPO_ROOT / "src" / "Gert.Api"),
            "--launch-profile",
            "FakeE2E",
            *(["--", *extra_config] if extra_config else []),
        ],
        cwd=str(REPO_ROOT),
    )


# --- real-vLLM mode (--vllm-url) ----------------------------------------------
def _normalize_vllm_url(url: str) -> str:
    """The adapters request absolute ``/v1/...`` paths against ``Gert:Vllm:BaseUrl``,
    so a pasted OpenAI-style base like ``http://host:8000/v1`` must lose the suffix."""
    url = url.rstrip("/")
    return url.removesuffix("/v1")


class _VllmModel(NamedTuple):
    id: str
    context: int | None
    capabilities: list[str]


# Minimal tool spec for the tools probe — mirrors VllmChatRequestBuilder (a
# `tools` array, tool_choice left at the server default).
_PROBE_TOOL = {
    "type": "function",
    "function": {
        "name": "probe",
        "description": "capability probe",
        "parameters": {"type": "object"},
    },
}
# 1x1 transparent PNG — the smallest valid image_url payload for the vision probe.
_PROBE_IMAGE = (
    "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJ"
    "AAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="
)


def _vllm_chat_probe(base_url: str, body: dict[str, object]) -> bool:
    """POST a tiny ``/v1/chat/completions`` and report whether the server accepts
    it (an unsupported feature 400s; acceptance proves the capability)."""
    req = urllib.request.Request(
        f"{base_url}/v1/chat/completions",
        data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=30):
            return True
    except urllib.error.HTTPError:
        return False


def _detect_vllm_models(base_url: str, only: str | None) -> list[_VllmModel]:
    """The models the real vLLM serves, enriched: context window straight from
    ``/v1/models``' ``max_model_len``, and tools/vision detected by max_tokens=1
    probe completions (there is no capability field on ``/v1/models``)."""
    with urllib.request.urlopen(f"{base_url}/v1/models", timeout=10) as resp:
        payload = json.loads(resp.read())
    entries = payload.get("data") or []
    if only is not None:
        entries = [e for e in entries if e["id"] == only] or [{"id": only}]
    if not entries:
        raise RuntimeError(f"{base_url}/v1/models returned no models")

    models: list[_VllmModel] = []
    for entry in entries:
        model_id: str = entry["id"]
        caps: list[str] = []
        if _vllm_chat_probe(
            base_url,
            {
                "model": model_id,
                "messages": [{"role": "user", "content": "ping"}],
                "max_tokens": 1,
                "tools": [_PROBE_TOOL],
            },
        ):
            caps.append("tools")
        if _vllm_chat_probe(
            base_url,
            {
                "model": model_id,
                "messages": [
                    {
                        "role": "user",
                        "content": [
                            {"type": "text", "text": "ping"},
                            {"type": "image_url", "image_url": {"url": _PROBE_IMAGE}},
                        ],
                    }
                ],
                "max_tokens": 1,
            },
        ):
            caps.append("vision")
        # No detected caps must still DECLARE capabilities: a null list means
        # "assumed tool-capable" to ModelInfo.SupportsTools (permissive default).
        models.append(
            _VllmModel(model_id, entry.get("max_model_len"), caps or ["text only"])
        )
    return models


def _vllm_overrides(base_url: str, models: list[_VllmModel]) -> list[str]:
    """Host config overrides pointing chat at the real vLLM: the adapter base URL,
    the chat model id, and a model-catalog entry per served model (real context +
    probed capabilities) instead of FakeE2E's 'Echo Server'."""
    overrides = [
        f"--Gert:Vllm:BaseUrl={base_url}",
        f"--Gert:Vllm:ChatModelId={models[0].id}",
    ]
    for i, m in enumerate(models):
        overrides += [
            f"--Gert:Models:{i}:Id={m.id}",
            f"--Gert:Models:{i}:Name={m.id}",
            f"--Gert:Models:{i}:Default={'true' if i == 0 else 'false'}",
            f"--Gert:Models:{i}:Endpoint={base_url}",
        ]
        if m.context is not None:
            overrides.append(f"--Gert:Models:{i}:Context={m.context}")
        overrides += [
            f"--Gert:Models:{i}:Capabilities:{j}={cap}"
            for j, cap in enumerate(m.capabilities)
        ]
    return overrides


def _wait_healthz(base_url: str, timeout: float = 90.0) -> bool:
    deadline = time.time() + timeout
    url = f"{base_url.rstrip('/')}/healthz"
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=2) as resp:
                if resp.status == 200:
                    return True
        except (urllib.error.URLError, ConnectionError, OSError):
            pass
        time.sleep(1.0)
    return False


# --- scenarios ---------------------------------------------------------------
# Each scenario is (name, fn(app_page, role) -> None) and asserts via Playwright.
# Kept deliberately small here; the richer assertions live in tests/*.py which
# this launcher can also drive. run.py is the integrated click-through.
def _scenario_chat(app: AppPage, role: str) -> None:
    from playwright.sync_api import expect

    app.composer.send("hello")
    expect(app.thread.bot_messages.last).to_be_visible()
    expect(app.thread.last_bot_body).to_contain_text("How can I help")


def _scenario_tool_cards(app: AppPage, role: str) -> None:
    from playwright.sync_api import expect

    app.composer.send("search my docs about qdrant")
    expect(app.thread.tool_cards.first).to_be_visible(timeout=15000)


def _scenario_chrome(app: AppPage, role: str) -> None:
    before = app.chrome.current_theme()
    app.chrome.toggle_theme()
    after = app.chrome.current_theme()
    assert after != before, "theme toggle did not change data-theme"


def _scenario_rbac(app: AppPage, role: str) -> None:
    from playwright.sync_api import expect

    app.page.goto(app.base_url + "/admin/users")
    if role == "admin":
        expect(app.page.locator(".utable")).to_be_visible(timeout=10000)
    else:
        # Non-admin: the table never appears (server enforces 403).
        expect(app.page.locator(".utable")).to_have_count(0)


def _scenario_artifact(app: AppPage, role: str) -> None:
    """The model's named html fence becomes a live canvas artifact (U7b extraction)."""
    from playwright.sync_api import expect

    app.composer.send("make me a demo html page")
    tab = app.canvas.tab("html")
    expect(tab).to_be_visible(timeout=15000)
    expect(tab).to_contain_text("demo.html")
    expect(app.canvas.html_iframe).to_be_visible(timeout=15000)


def _scenario_memory(app: AppPage, role: str) -> None:
    """A memory entry rides the hybrid query: seeded via the SPA's own memory
    service, retrieved by the model's search_documents call, and surfaced with
    its DECODED title (never the base64 blob) plus a citation."""
    from playwright.sync_api import expect

    app.page.evaluate(
        """async () => {
            const memory = await import('/services/memory.js');
            await memory.add({
                title: 'Favorite database',
                content: 'My favorite database is sqlite-vec, running the homelab RAG stack.',
            });
        }"""
    )
    app.composer.send("search my docs about favorite database")
    card = app.thread.tool_cards.first
    expect(card).to_be_visible(timeout=15000)
    app.thread.expand_tool_card(card)
    expect(card.locator(".doc-hit").first).to_contain_text(
        "Favorite database", timeout=15000
    )
    expect(app.thread.last_bot_body).to_contain_text(
        "sqlite-vec is your favorite", timeout=15000
    )
    expect(app.thread.citations.first).to_be_visible(timeout=15000)


def _scenario_todos(app: AppPage, role: str) -> None:
    """The set_todos tool renders the model-managed checklist; a role without the
    `todo` entitlement proves the execution-time ceiling (card errors, turn survives)."""
    from playwright.sync_api import expect

    app.composer.send("plan the homelab upgrade")
    expect(app.thread.tool_cards.first).to_be_visible(timeout=15000)
    if role == "admin":
        # The todo card auto-opens; three rows with their statuses.
        expect(app.thread.todo_rows).to_have_count(3, timeout=15000)
        expect(app.thread.todo_rows.first).to_contain_text("Order the new SSD")
        expect(app.thread.root.locator(".tcard .todo-row.active")).to_contain_text(
            "Migrate rag.db"
        )
        # The single card's header shows done/total + the progress bar (1 of 3).
        expect(app.thread.root.locator(".tcard .tcount")).to_have_text("1/3")
        expect(app.thread.root.locator(".tcard .pbar")).to_have_attribute(
            "aria-valuenow", "1"
        )
    else:
        # `user` carries gert_tools "rag search": set_todos is refused at
        # execution time, the card errors, and the turn still finishes.
        expect(app.thread.errored_tool_cards.first).to_be_visible(timeout=15000)
    expect(app.thread.last_bot_body).to_contain_text("Plan is up", timeout=15000)


def _scenario_clock(app: AppPage, role: str) -> None:
    """The get_datetime tool puts a wall-clock reading on the card (entitled roles)."""
    import re

    from playwright.sync_api import expect

    app.composer.send("what time is it")
    card = app.thread.tool_cards.first
    expect(card).to_be_visible(timeout=15000)
    if role == "admin":
        app.thread.expand_tool_card(card)
        expect(app.thread.tool_stdout.first).to_contain_text(
            re.compile(r"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \(UTC"), timeout=15000
        )
    else:
        expect(app.thread.errored_tool_cards.first).to_be_visible(timeout=15000)
    expect(app.thread.last_bot_body).to_contain_text("on the card above", timeout=15000)


SCENARIOS = {
    "chat": _scenario_chat,
    "tool_cards": _scenario_tool_cards,
    "chrome": _scenario_chrome,
    "rbac": _scenario_rbac,
    "artifact": _scenario_artifact,
    "memory": _scenario_memory,
    "todos": _scenario_todos,
    "clock": _scenario_clock,
}


def _run_matrix(
    base_url: str, browsers: list[str], roles: list[str], headed: bool, keep_open: bool
) -> list[MatrixResult]:
    from playwright.sync_api import sync_playwright

    from .pages import AppPage

    ARTIFACTS_DIR.mkdir(parents=True, exist_ok=True)
    results: list[MatrixResult] = []

    with sync_playwright() as pw:
        for browser_name in browsers:
            browser_type = getattr(pw, browser_name)
            browser = browser_type.launch(headless=not headed)
            for role in roles:
                token = tokens.mint(role)
                context = browser.new_context()
                context.add_init_script(_init_script(token))
                context.tracing.start(screenshots=True, snapshots=True)
                page = context.new_page()
                # Surface SPA load-time failures: console errors + uncaught page errors.
                # Default-bind the per-iteration list into each handler (B023).
                console_errors: list[str] = []

                def _on_console(
                    m: ConsoleMessage, errs: list[str] = console_errors
                ) -> None:
                    if m.type == "error":
                        errs.append(f"{m.type}: {m.text}")

                def _on_pageerror(e: Error, errs: list[str] = console_errors) -> None:
                    errs.append(f"pageerror: {e}")

                page.on("console", _on_console)
                page.on("pageerror", _on_pageerror)
                app = AppPage(page)
                app.base_url = base_url

                # `limited` is RBAC-only; others run the full click-through.
                scenario_names = ["rbac"] if role == "limited" else list(SCENARIOS)

                stem_base = f"{browser_name}-{role}"
                for name in scenario_names:
                    ok, detail = True, ""
                    try:
                        app.goto(base_url, "/")
                        app.wait_ready()
                        SCENARIOS[name](app, role)
                    except Exception as exc:
                        errs = " | ".join(console_errors[-5:])
                        ok, detail = (
                            False,
                            f"{exc!r}{'  console: ' + errs if errs else ''}",
                        )
                        page.screenshot(
                            path=str(ARTIFACTS_DIR / f"{stem_base}-{name}.png")
                        )
                    results.append((browser_name, role, name, ok, detail))

                if keep_open:
                    input(
                        f"[{browser_name}/{role}] keep-open — press Enter to continue…"
                    )
                context.tracing.stop(path=str(ARTIFACTS_DIR / f"{stem_base}.zip"))
                context.close()
            browser.close()

    return results


def _serve(base_url: str, role: str) -> None:
    """Open ONE headed browser signed in as ``role`` and keep it open for manual
    click-through against the running mocks+host. Same F2-safe token injection as the
    matrix (an init script seeds the in-memory bearer; nothing in localStorage)."""
    from playwright.sync_api import sync_playwright

    token = tokens.mint(role)
    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=False)
        context = browser.new_context()
        context.add_init_script(_init_script(token))
        page = context.new_page()
        app = AppPage(page)
        app.base_url = base_url
        app.goto(base_url, "/")
        app.wait_ready()
        print(f"\n  Gert is live at {base_url}  (signed in as '{role}').")
        print("  A browser window is open — click around. Press Enter here to quit.\n")
        input()
        context.close()
        browser.close()


def _run_pytest(base_url: str, browsers: list[str]) -> int:
    """Drive the FULL tests/*.py suite — component/harness mounts AND the
    integration assertions (chat, knowledge, rbac, llm-tools) — against the
    already-booted host, then fold the result into the gate. Runs from the repo
    root (so ``tools.smoke`` imports resolve) against the same FakeE2E host the
    matrix used. The security-relevant integration tests (RBAC/SSRF/IDOR) MUST
    gate: they once sat outside the gate behind ``-m component`` and silently
    rotted. Returns the pytest exit code (0 = pass)."""
    print("\nRunning pytest suite (component + integration)…")
    browser_flags = [f"--browser={b}" for b in browsers]
    proc = subprocess.run(
        [
            sys.executable,
            "-m",
            "pytest",
            str(Path(__file__).resolve().parent / "tests"),
            f"--gert-base-url={base_url}",
            *browser_flags,
            "-q",
        ],
        cwd=str(REPO_ROOT),
        check=False,
    )
    return proc.returncode


def _run_api_smoke(base_url: str) -> int:
    """Run ONLY the browserless API auth smoke (``tests/test_auth_smoke.py``)
    against the booted host — no Playwright, no matrix. The cheap CI gate proving
    every ``/api`` endpoint rejects missing/invalid bearer tokens. Returns the
    pytest exit code (0 = pass)."""
    print("\nRunning API auth smoke (tests/test_auth_smoke.py)…")
    proc = subprocess.run(
        [
            sys.executable,
            "-m",
            "pytest",
            str(Path(__file__).resolve().parent / "tests" / "test_auth_smoke.py"),
            f"--gert-base-url={base_url}",
            "-q",
        ],
        cwd=str(REPO_ROOT),
        check=False,
    )
    return proc.returncode


def _report(results: list[MatrixResult]) -> int:
    failures = [r for r in results if not r[3]]
    for browser, role, name, ok, detail in results:
        status = "PASS" if ok else "FAIL"
        line = f"  [{status}] {browser:8} {role:8} {name}"
        if not ok:
            line += f"  -> {detail}"
        print(line)
    print(f"\n{len(results) - len(failures)}/{len(results)} scenarios passed.")
    return 1 if failures else 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Gert E2E launcher.")
    parser.add_argument("--browser", choices=[*BROWSERS, "all"], default="all")
    parser.add_argument(
        "--role", choices=["admin", "user", "limited", "all"], default="all"
    )
    parser.add_argument("--headed", action="store_true")
    parser.add_argument(
        "--pytest",
        action="store_true",
        help="After the matrix, run the richer tests/*.py assertions against the "
        "same booted host. The gate fails if either the matrix or pytest fails.",
    )
    parser.add_argument(
        "--api-smoke",
        action="store_true",
        help="Boot mocks + host and run ONLY the browserless API auth smoke "
        "(tests/test_auth_smoke.py) — no Playwright, no matrix. The cheap CI "
        "gate for the auth surface.",
    )
    parser.add_argument("--keep-open", action="store_true")
    parser.add_argument(
        "--serve",
        action="store_true",
        help="Boot mocks + host, open ONE headed browser and keep it open for manual "
        "click-through (no scenarios). Use --role to pick the identity.",
    )
    parser.add_argument(
        "--proxy",
        action="store_true",
        help="Boot mocks + host + a dev reverse-proxy (no Playwright). Open the printed "
        "URL in your OWN browser — the proxy injects a dev bearer. Use --role.",
    )
    parser.add_argument(
        "--proxy-port", type=int, default=5180, help="Port for --proxy (default 5180)."
    )
    parser.add_argument(
        "--base-url",
        default=None,
        help="Attach to an already-running host+mocks instead of booting them.",
    )
    parser.add_argument(
        "--vllm-url",
        default=None,
        help="Point chat at a REAL vLLM (e.g. http://192.168.10.99:8000/v1) instead "
        "of the python mock; auth + SearXNG stay mocked. A trailing /v1 is stripped. "
        "Embeddings share the same upstream, so knowledge-upload needs the real "
        "server to also host Gert:Vllm:EmbeddingModelId.",
    )
    parser.add_argument(
        "--vllm-model",
        default=None,
        help="Restrict the catalog to one model id on the real vLLM. Default: every "
        "model <vllm-url>/v1/models reports, each with detected context + "
        "tools/vision capabilities.",
    )
    parser.add_argument(
        "--searxng-url",
        default=None,
        help="Point web search at a REAL SearXNG (e.g. https://searx.zaggy.nl) "
        "instead of the python mock. The instance must allow format=json.",
    )
    args = parser.parse_args(argv)

    browsers = BROWSERS if args.browser == "all" else [args.browser]
    roles = [*MATRIX_ROLES, "limited"] if args.role == "all" else [args.role]

    # Ensure the dev keypair + JWKS exist before the host boots (so it can trust it).
    tokens.ensure_keypair()

    mocks: list[_ServerThread] = []
    host_proc: subprocess.Popen[bytes] | None = None
    base_url = args.base_url or DEFAULT_HOST

    try:
        if args.base_url is None:
            real_vllm = args.vllm_url is not None
            real_searxng = args.searxng_url is not None
            extra_config: list[str] = []
            if real_vllm:
                vllm_url = _normalize_vllm_url(args.vllm_url)
                vllm_models = _detect_vllm_models(vllm_url, args.vllm_model)
                extra_config += _vllm_overrides(vllm_url, vllm_models)
                print(f"Chat -> REAL vLLM at {vllm_url}:")
                for m in vllm_models:
                    ctx = f"{m.context} ctx" if m.context is not None else "ctx unknown"
                    print(f"  {m.id}: {ctx}, {'/'.join(m.capabilities)}")
            if real_searxng:
                searxng_url = args.searxng_url.rstrip("/")
                extra_config.append(f"--Gert:Search:BaseUrl={searxng_url}")
                print(f"Search -> REAL SearXNG at {searxng_url}")
            mocked = [
                name
                for name, mock_it in [
                    ("vLLM", not real_vllm),
                    ("SearXNG", not real_searxng),
                ]
                if mock_it
            ]
            if mocked:
                print(f"Booting mock upstreams ({' + '.join(mocked)})…")
            mocks = _boot_mocks(
                include_vllm=not real_vllm, include_searxng=not real_searxng
            )
            time.sleep(1.0)
            # Deterministic runs: the host's user data is recreated from scratch on
            # every harness-owned boot, so stale state from a previous/killed run
            # can never leak into this one. (--base-url attaches to a host we don't
            # own, so its data is left alone.)
            shutil.rmtree(DATA_ROOT, ignore_errors=True)
            print("Booting FakeE2E host (dotnet run)…")
            host_proc = _boot_host(extra_config)

        print(f"Waiting for {base_url}/healthz …")
        if not _wait_healthz(base_url):
            print("Host did not become healthy in time.", file=sys.stderr)
            return 2

        if args.proxy:
            role = args.role if args.role != "all" else "admin"
            app = make_proxy_app(base_url, tokens.mint(role))
            print(
                f"\n  Open http://127.0.0.1:{args.proxy_port} in your browser "
                f"(signed in as '{role}')."
            )
            print(
                f"  Proxying to {base_url} with a dev bearer injected. Ctrl+C to stop.\n"
            )
            uvicorn.run(
                app, host="127.0.0.1", port=args.proxy_port, log_level="warning"
            )
            return 0

        if args.serve:
            _serve(base_url, args.role if args.role != "all" else "admin")
            return 0

        if args.api_smoke:
            return _run_api_smoke(base_url)

        print("Running scenario matrix…")
        results = _run_matrix(base_url, browsers, roles, args.headed, args.keep_open)
        matrix_rc = _report(results)
        if args.pytest:
            pytest_rc = _run_pytest(base_url, browsers)
            return matrix_rc or pytest_rc
        return matrix_rc
    finally:
        if host_proc is not None:
            host_proc.terminate()
            try:
                host_proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                host_proc.kill()
        for s in mocks:
            s.stop()


if __name__ == "__main__":
    raise SystemExit(main())
