"""shots.py - regenerate the marketing screenshots under ``site/assets/``.

Boots the same FakeE2E host + Python mock upstreams as the E2E launcher
(:mod:`tools.smoke.run`), drives a few deterministic ``fixtures.json`` scenarios,
and captures 1440x900 @2x PNGs for the landing page and README. This is NOT part
of the CI gate - run it by hand after a UI change that dates the shots::

    tools/smoke/.venv/bin/python -m tools.smoke.shots

Same F2-safe token injection as the launcher: an init script seeds the in-memory
bearer; nothing is read from localStorage. Browsers must be installed
(``uv run playwright install chromium``).
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import time

from playwright.sync_api import Locator, expect, sync_playwright

from . import tokens
from .pages import AppPage
from .run import (
    DATA_ROOT,
    DEFAULT_HOST,
    REPO_ROOT,
    _boot_host,
    _boot_mocks,
    _init_script,
    _wait_healthz,
)

# Where the landing page / README read their shots from.
ASSETS_DIR = REPO_ROOT / "site" / "assets"
# 1440x900 at devicePixelRatio 2 -> the 2880x1800 PNGs the page references.
SCALE = 2.0
# data-theme values the SPA writes (state/ui.js): manila = paper, ember = dark.
LIGHT = "manila"
DARK = "ember"


# --- turn / chrome helpers ---------------------------------------------------
def _send(app: AppPage, text: str) -> None:
    """Send one message and wait for the turn to fully settle (a new bot bubble
    appears, then the composer swaps its stop button back to send)."""
    before = app.thread.bot_messages.count()
    app.composer.send(text)
    expect(app.thread.bot_messages).to_have_count(before + 1, timeout=30000)
    expect(app.page.locator(".composer .stop")).to_have_count(0, timeout=30000)


def _new_chat(app: AppPage) -> None:
    app.sidebar.new_chat.click()
    expect(app.thread.messages).to_have_count(0, timeout=10000)


def _ensure_activity_open(act: Locator) -> None:
    if "open" not in (act.get_attribute("class") or ""):
        act.locator(".act-head").click()


def _ensure_card_open(card: Locator) -> None:
    """Expand a tool card's body (query / doc-hits / stdout / todo rows). Reads the
    body's hidden class first so a re-run never toggles an already-open card shut."""
    body = card.locator(".tbody")
    if "hide" in (body.get_attribute("class") or ""):
        card.locator(".thead").click()


def _reveal_tools(app: AppPage) -> None:
    """Open every present activity dropdown and expand the cards inside, so the
    tool work (RAG hits, sandbox stdout, the todo checklist) is on screen."""
    acts = app.page.locator(".activity:not(.none)")
    for i in range(acts.count()):
        _ensure_activity_open(acts.nth(i))
    cards = app.page.locator(".activity:not(.none) .tcard")
    for i in range(cards.count()):
        _ensure_card_open(cards.nth(i))


def _set_theme(app: AppPage, name: str) -> None:
    app.page.evaluate("(n) => import('/state/ui.js').then((m) => m.setTheme(n))", name)
    expect(app.page.locator("html")).to_have_attribute("data-theme", name, timeout=5000)


def _shoot(app: AppPage, name: str, theme: str) -> None:
    _set_theme(app, theme)
    # Drop any focus ring / caret so the still frame is clean.
    app.page.evaluate("() => document.activeElement && document.activeElement.blur()")
    app.page.wait_for_timeout(400)
    ASSETS_DIR.mkdir(parents=True, exist_ok=True)
    app.page.screenshot(path=str(ASSETS_DIR / f"{name}.png"))
    print(f"  wrote {name}.png ({theme})")


# --- the scenes --------------------------------------------------------------
def _scene_chat(app: AppPage) -> None:
    """A three-turn thread: a plain answer (names the conversation), a doc search
    with an inline citation, and a sandboxed Python run - the hero shot."""
    _new_chat(app)
    _send(app, "should I use Qdrant or sqlite-vec?")
    _send(app, "search my docs about qdrant")
    _send(app, "run python to add two and two")
    _reveal_tools(app)
    app.thread.bot_messages.last.scroll_into_view_if_needed()
    _shoot(app, "chat-light", LIGHT)
    _shoot(app, "chat-dark", DARK)


def _scene_canvas(app: AppPage) -> None:
    """Two artifacts (a Python file and an HTML page) open as canvas tabs, with the
    HTML page live-previewing on the right."""
    _new_chat(app)
    _send(app, "write a python fibonacci script")
    _send(app, "make me a demo html page")
    app.page.locator(".artifact-chip", has_text="demo.html").last.click()
    expect(app.canvas.tab("html")).to_be_visible(timeout=10000)
    expect(app.canvas.html_iframe).to_be_visible(timeout=10000)
    _shoot(app, "canvas-light", LIGHT)
    _shoot(app, "canvas-dark", DARK)


def _scene_todos(app: AppPage) -> None:
    """The model-managed checklist: one step done, one in progress, one pending."""
    _new_chat(app)
    _send(app, "plan the homelab upgrade")
    _reveal_tools(app)
    _shoot(app, "todos-light", LIGHT)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Regenerate the site/assets shots.")
    parser.add_argument(
        "--headed", action="store_true", help="Show the browser (debugging)."
    )
    args = parser.parse_args(argv)

    tokens.ensure_keypair()
    print("Booting mock upstreams (vLLM + SearXNG + monty)...")
    mocks = _boot_mocks(include_monty=True)
    time.sleep(1.0)
    # Fresh user data so the threads/sidebar are exactly the seeded ones.
    shutil.rmtree(DATA_ROOT, ignore_errors=True)
    print("Booting FakeE2E host (dotnet run)...")
    host: subprocess.Popen[bytes] = _boot_host()
    try:
        print(f"Waiting for {DEFAULT_HOST}/healthz ...")
        if not _wait_healthz(DEFAULT_HOST):
            print("Host did not become healthy in time.", file=sys.stderr)
            return 2

        token = tokens.mint("admin")
        with sync_playwright() as pw:
            browser = pw.chromium.launch(headless=not args.headed)
            context = browser.new_context(
                viewport={"width": 1440, "height": 900},
                device_scale_factor=SCALE,
                timezone_id="UTC",
            )
            context.add_init_script(_init_script(token))
            page = context.new_page()
            app = AppPage(page)
            app.base_url = DEFAULT_HOST
            app.goto(DEFAULT_HOST, "/")
            app.wait_ready()

            print("Capturing shots ->", ASSETS_DIR)
            _scene_chat(app)
            _scene_canvas(app)
            _scene_todos(app)

            context.close()
            browser.close()
        print("Done.")
        return 0
    finally:
        host.terminate()
        try:
            host.wait(timeout=10)
        except subprocess.TimeoutExpired:
            host.kill()
        for s in mocks:
            s.stop()


if __name__ == "__main__":
    raise SystemExit(main())
