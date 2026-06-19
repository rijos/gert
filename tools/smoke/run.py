"""run.py - the Gert E2E launcher (testing.md section 9).

In order:

1. **Boot the mock upstreams** (vLLM + SearXNG) on localhost, then **boot the
   host** with ``dotnet run --launch-profile FakeE2E`` (whose config points the
   real Gert.Chat/Tools clients at the mock URLs). Or attach to an already-
   running pair with ``--base-url``. Wait for ``/healthz``.
2. **Mint tokens** in-process via :mod:`tools.smoke.tokens` (no HTTP round-trip).
3. **Inject + drive** - for each ``(browser, role)`` in the matrix, seed the token
   and run the scenarios.
4. **Report** pass/fail per scenario; trace + screenshot on failure under
   ``tools/smoke/artifacts/``.

Token injection note: ``services/auth.js`` keeps the access token in an in-memory
module variable (security F2) - it is NEVER read from localStorage. So the
launcher injects ``window.GERT_DEV_TOKEN`` via a Playwright **init script** (runs
before any app module), and a dev-only branch in ``ensureSession`` consumes it.
This is gated by the presence of the injected global, which production never sets.

Flags: ``--browser``, ``--role``, ``--headed``, ``--keep-open``, ``--base-url``
(attach to an already-running host+mocks instead of booting a fresh pair).

This launcher needs browsers installed (``uv run playwright install chromium
firefox``) - only the CI/staging web job has them. The non-browser parts (token
mint, specs conformance, mocks boot, ``--api-smoke``) run without browsers; see
the README.
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

import uvicorn
from playwright.sync_api import ConsoleMessage, Error
from starlette.applications import Starlette

from . import tokens
from .mocks import MONTY_PORT, SEARXNG_PORT, VLLM_PORT
from .mocks.monty import app as monty_app
from .mocks.searxng import app as searxng_app
from .mocks.vllm import app as vllm_app
from .pages import AppPage
from .proxy import make_proxy_app

# A matrix result row: (browser, role, scenario, ok, detail).
type MatrixResult = tuple[str, str, str, bool, str]

REPO_ROOT = Path(__file__).resolve().parents[2]
ARTIFACTS_DIR = Path(__file__).resolve().parent / "artifacts"
# The FakeE2E host's user-data root (Storage__DataRoot, resolved against src/Gert.Api/).
# Wiped on every harness-owned boot - NOT the sibling .dev/jwt keypair, which is
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


def _boot_mocks(*, include_monty: bool = True) -> list[_ServerThread]:
    servers: list[_ServerThread] = [
        _ServerThread(vllm_app, VLLM_PORT),
        _ServerThread(searxng_app, SEARXNG_PORT),
    ]
    if include_monty:
        servers.append(_ServerThread(monty_app, MONTY_PORT))
    for s in servers:
        s.start()
    return servers


def _boot_real_monty() -> subprocess.Popen[bytes]:
    """Boot the REAL tools/monty sidecar (pydantic-monty) on MONTY_PORT in this venv.

    serve-mock uses this so run_python executes arbitrary Python on the real interpreter;
    the deterministic mock (mocks/monty.py) is skipped when this runs. The sidecar shares
    this harness's venv, where ``uv sync`` has installed pydantic-monty.
    """
    return subprocess.Popen(
        [sys.executable, str(REPO_ROOT / "tools" / "monty" / "app.py")],
        env={
            **os.environ,
            "GERT_MONTY_HOST": "127.0.0.1",
            "GERT_MONTY_PORT": str(MONTY_PORT),
        },
    )


# --- the host ----------------------------------------------------------------
def _boot_host(*, web_root: str | None = None) -> subprocess.Popen[bytes]:
    env = os.environ.copy()
    if web_root is not None:
        # ASPNETCORE_WEBROOT overrides the served wwwroot. FakeE2E's launchSettings
        # doesn't set it, so this process-env value reaches the web host - pointing
        # static files at a bundled copy instead of the raw source (--minify).
        env["ASPNETCORE_WEBROOT"] = web_root
        # ...but in Development the static-web-assets manifest re-maps the app's
        # assets back to the SOURCE wwwroot, shadowing ASPNETCORE_WEBROOT. Point the
        # manifest at a path that doesn't exist so StaticWebAssetsLoader resolves
        # nothing and the plain physical WebRootFileProvider (our bundled copy) wins.
        env["ASPNETCORE_STATICWEBASSETS"] = str(Path(web_root) / ".no-swa-manifest")
    return subprocess.Popen(
        [
            "dotnet",
            "run",
            "--project",
            str(REPO_ROOT / "src" / "Gert.Api"),
            "--launch-profile",
            "FakeE2E",
        ],
        cwd=str(REPO_ROOT),
        env=env,
    )


# --- bundled web root (--minify) ---------------------------------------------
def _prepare_bundled_webroot() -> str:
    """Copy src/Gert.Api/wwwroot into a temp dir, bundle it via the real release tool
    (tools/Gert.Web.Bundle, pinned esbuild), and return the temp dir.

    Lets serve-mock --minify exercise the exact assets that ship on publish (the same
    esbuild bundle -> app.js + app.css, index.html repointed, raw source pruned;
    ui-components.md section 6) without touching the working tree. First run fetches the
    pinned esbuild binary (no npm). The caller removes the dir on shutdown.
    """
    src = REPO_ROOT / "src" / "Gert.Api" / "wwwroot"
    dest = tempfile.mkdtemp(prefix="gert-bundled-www-")
    print(f"Bundle: copying wwwroot -> {dest}")
    shutil.copytree(src, dest, dirs_exist_ok=True)
    print("Bundle: running tools/Gert.Web.Bundle (esbuild)...")
    subprocess.run(
        [
            "dotnet",
            "run",
            "--project",
            str(REPO_ROOT / "tools" / "Gert.Web.Bundle"),
            "--no-launch-profile",
            "--",
            dest,
        ],
        cwd=str(REPO_ROOT),
        check=True,
    )
    return dest


def _prepare_transpiled_webroot() -> str:
    """Build the DEV served web root and return it: esbuild-transpiles wwwroot's .ts -> sibling
    .js into a temp mirror (typescript-migration.md section 3), via the real tool
    (tools/Gert.Web.Bundle, pinned esbuild). wwwroot stays source-only; the mirror carries only
    .js + assets (the .ts/.d.ts/tsconfig are pruned). This is the DEFAULT boot path (e2e / serve /
    proxy), so the smoke suite exercises the exact transpiled modules the dev hosts serve - URLs
    resolve unchanged (still /lib/*.js, /components/*.js). --minify swaps in the release bundle
    instead. The tool owns the copy; first run fetches the pinned esbuild binary (no npm). The
    caller removes the dir on shutdown.
    """
    src = REPO_ROOT / "src" / "Gert.Api" / "wwwroot"
    dest = tempfile.mkdtemp(prefix="gert-transpiled-www-")
    print(f"Transpile: building dev mirror -> {dest}")
    subprocess.run(
        [
            "dotnet",
            "run",
            "--project",
            str(REPO_ROOT / "tools" / "Gert.Web.Bundle"),
            "--no-launch-profile",
            "--",
            "--transpile",
            str(src),
            dest,
        ],
        cwd=str(REPO_ROOT),
        check=True,
    )
    return dest


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
    app.thread.open_activity()
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
    """The model's make_artifact call becomes a live canvas artifact for an entitled
    role; a role without `make_artifact` in gert_tools hits the execution-time ceiling
    (card errors, turn survives) - the same per-tool proof as todos/clock."""
    from playwright.sync_api import expect

    app.composer.send("make me a demo html page")
    if role == "admin":
        tab = app.canvas.tab("html")
        expect(tab).to_be_visible(timeout=15000)
        expect(tab).to_contain_text("demo.html")
        expect(app.canvas.html_iframe).to_be_visible(timeout=15000)
    expect(app.thread.last_bot_body).to_contain_text("in the canvas", timeout=15000)
    if role != "admin":
        # `user` lacks the make_artifact entitlement: the call is dropped by the
        # ceiling and stays silent toward the user (auth.md - the boundary does not
        # leak into the conversation). The turn still finishes, but nothing
        # surfaces: no canvas tab, no tool card.
        expect(app.canvas.tab("html")).to_have_count(0)
        expect(app.thread.tool_cards).to_have_count(0)


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
    app.thread.open_activity()
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
    if role == "admin":
        app.thread.open_activity()
        expect(app.thread.tool_cards.first).to_be_visible(timeout=15000)
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
    expect(app.thread.last_bot_body).to_contain_text("Plan is up", timeout=15000)
    if role != "admin":
        # `user` lacks the todo entitlement: set_todos is dropped by the ceiling and
        # stays silent toward the user (auth.md). The turn finishes, but no card and
        # no checklist surface.
        expect(app.thread.tool_cards).to_have_count(0)
        expect(app.thread.todo_rows).to_have_count(0)


def _scenario_clock(app: AppPage, role: str) -> None:
    """The get_datetime tool puts a wall-clock reading on the card (entitled roles)."""
    import re

    from playwright.sync_api import expect

    app.composer.send("what time is it")
    if role == "admin":
        app.thread.open_activity()
        card = app.thread.tool_cards.first
        expect(card).to_be_visible(timeout=15000)
        app.thread.expand_tool_card(card)
        expect(app.thread.tool_stdout.first).to_contain_text(
            re.compile(r"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \(UTC"), timeout=15000
        )
    expect(app.thread.last_bot_body).to_contain_text("on the card above", timeout=15000)
    if role != "admin":
        # `user` lacks the clock entitlement: get_datetime is dropped by the ceiling
        # and stays silent toward the user (auth.md) - the turn finishes, no card.
        expect(app.thread.tool_cards).to_have_count(0)


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
                # Pin the browser timezone so clock-dependent scenarios are
                # deterministic regardless of the host machine's locale.
                context = browser.new_context(timezone_id="UTC")
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
                        f"[{browser_name}/{role}] keep-open - press Enter to continue..."
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
        print("  A browser window is open - click around. Press Enter here to quit.\n")
        input()
        context.close()
        browser.close()


def _run_pytest(base_url: str, browsers: list[str]) -> int:
    """Drive the FULL tests/*.py suite - component/harness mounts AND the
    integration assertions (chat, knowledge, rbac, llm-tools) - against the
    already-booted host, then fold the result into the gate. Runs from the repo
    root (so ``tools.smoke`` imports resolve) against the same FakeE2E host the
    matrix used. The security-relevant integration tests (RBAC/SSRF/IDOR) MUST
    gate: they once sat outside the gate behind ``-m component`` and silently
    rotted. Returns the pytest exit code (0 = pass)."""
    print("\nRunning pytest suite (component + integration)...")
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
    against the booted host - no Playwright, no matrix. The cheap CI gate proving
    every ``/api`` endpoint rejects missing/invalid bearer tokens. Returns the
    pytest exit code (0 = pass)."""
    print("\nRunning API auth smoke (tests/test_auth_smoke.py)...")
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
        "(tests/test_auth_smoke.py) - no Playwright, no matrix. The cheap CI "
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
        "URL in your OWN browser - the proxy injects a dev bearer. Use --role.",
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
        "--monty-real",
        action="store_true",
        help="Run run_python on the REAL monty interpreter (the tools/monty sidecar, "
        "needs pydantic-monty) instead of the deterministic mock. Used by serve-mock so "
        "you can execute arbitrary Python in the browser; the CI suite keeps the mock.",
    )
    parser.add_argument(
        "--minify",
        action="store_true",
        help="Serve a BUNDLED copy of wwwroot (the real release esbuild pass -> app.js + "
        "app.css) instead of raw source, so you can eyeball the bundle in a browser. "
        "Ignored with --base-url (we only bundle a host we boot ourselves).",
    )
    args = parser.parse_args(argv)

    browsers = BROWSERS if args.browser == "all" else [args.browser]
    roles = [*MATRIX_ROLES, "limited"] if args.role == "all" else [args.role]

    # Ensure the dev keypair + JWKS exist before the host boots (so it can trust it).
    tokens.ensure_keypair()

    mocks: list[_ServerThread] = []
    host_proc: subprocess.Popen[bytes] | None = None
    monty_proc: subprocess.Popen[bytes] | None = None
    built_root: str | None = None
    base_url = args.base_url or DEFAULT_HOST

    try:
        if args.base_url is None:
            real_monty = args.monty_real
            mocked = ["vLLM", "SearXNG", *(["monty"] if not real_monty else [])]
            print(f"Booting mock upstreams ({' + '.join(mocked)})...")
            mocks = _boot_mocks(include_monty=not real_monty)
            if real_monty:
                print(f"Sandbox -> REAL monty sidecar (tools/monty) on :{MONTY_PORT}")
                monty_proc = _boot_real_monty()
            time.sleep(1.0)
            # Deterministic runs: the host's user data is recreated from scratch on
            # every harness-owned boot, so stale state from a previous/killed run
            # can never leak into this one. (--base-url attaches to a host we don't
            # own, so its data is left alone.)
            shutil.rmtree(DATA_ROOT, ignore_errors=True)
            # Serve the SPA from a BUILT web root (typescript-migration.md section 3): the dev
            # default is the esbuild transpile mirror (.ts -> .js); --minify serves the release
            # bundle instead. The browserless --api-smoke never loads the SPA, so it skips the
            # build entirely and serves the raw source - keeping the cheap auth gate cheap.
            if not args.api_smoke:
                built_root = (
                    _prepare_bundled_webroot()
                    if args.minify
                    else _prepare_transpiled_webroot()
                )
            print("Booting FakeE2E host (dotnet run)...")
            host_proc = _boot_host(web_root=built_root)

        print(f"Waiting for {base_url}/healthz ...")
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

        print("Running scenario matrix...")
        results = _run_matrix(base_url, browsers, roles, args.headed, args.keep_open)
        matrix_rc = _report(results)
        if args.pytest:
            pytest_rc = _run_pytest(base_url, browsers)
            return matrix_rc or pytest_rc
        return matrix_rc
    finally:
        for proc in (host_proc, monty_proc):
            if proc is not None:
                proc.terminate()
                try:
                    proc.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    proc.kill()
        for s in mocks:
            s.stop()
        if built_root is not None:
            shutil.rmtree(built_root, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
