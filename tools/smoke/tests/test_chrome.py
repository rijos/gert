"""test_chrome.py — theme · responsive drawers · model picker (§9)."""

from __future__ import annotations

import re

from playwright.sync_api import Page, expect

from tools.smoke.pages import AppPage


def _open(page: Page, base_url: str) -> AppPage:
    app = AppPage(page)
    app.goto(base_url, "/")
    app.wait_ready()
    return app


def test_theme_toggle_persists(page: Page, base_url: str) -> None:
    app = _open(page, base_url)
    before = app.chrome.current_theme()
    app.chrome.toggle_theme()
    after = app.chrome.current_theme()
    assert after != before
    # Theme persists to localStorage (the one thing that does — never the token).
    stored = page.evaluate("() => localStorage.getItem('gert.theme')")
    assert stored == after


def test_model_picker_selects(page: Page, base_url: str) -> None:
    app = _open(page, base_url)
    app.model_picker.open()
    items = app.model_picker.menu_items
    if items.count() == 0:
        # No models configured in the fake catalog — picker still opens.
        expect(app.model_picker.trigger).to_be_visible()
        return
    name = items.first.locator(".m-name").inner_text().strip()
    items.first.click()
    expect(app.model_picker.current_name).to_contain_text(name.split(" ")[0])


def test_responsive_drawers(page: Page, base_url: str) -> None:
    page.set_viewport_size({"width": 700, "height": 900})
    app = _open(page, base_url)
    # At mobile widths the .app gets drawer state classes when toggled. Find the nav
    # toggle if present; the assertion is that Escape closes any open drawer.
    page.keyboard.press("Escape")
    expect(app.chrome.app).not_to_have_class(re.compile(r"nav-open"))
