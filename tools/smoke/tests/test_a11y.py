"""test_a11y.py - a small set of WCAG 2.2 AA regression guards.

These pin the load-bearing accessibility contracts of the SPA's shared primitives and
chrome - the ones that, if they regress, silently lock out keyboard and screen-reader
users across the whole app:

  - the Switch is a real role=switch control (keyboard-operable, state exposed),
  - a Modal is a focus-managed dialog,
  - Icon() is decorative by default (one factory guards ~56 call sites),
  - the page exposes a <main> landmark + skip link + a per-view title,
  - the toast host is a live region.

The first three mount the real primitive in tests/web/harness.html (``@pytest.mark.component``,
like test_components/test_style); the last two drive the full app via base_url. This is NOT a
full audit - it is a thin tripwire over the highest-impact controls (testing.md section 7).
"""

from __future__ import annotations

import re

import pytest
from playwright.sync_api import Page, expect

from tools.smoke.pages import AppPage


@pytest.mark.component
def test_switch_is_a_keyboard_operable_role_switch(page: Page, base_url: str) -> None:
    """WCAG 2.1.1 / 4.1.2: the Switch backs every tool toggle, the rag/pin toggles and the
    knowledge use-in-chat switch. It must be focusable, operable by Space, and expose
    aria-checked - not a bare <div onclick> (which is invisible + inert to AT)."""
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const van = (await import('/lib/van.js')).default;
            const { Switch } = await import('/components/ui/switch.js');
            const on = van.state(false);
            window.__mount(Switch({
                on: () => on.val,
                onToggle: () => (on.val = !on.val),
                label: 'Demo toggle',
            }));
        }"""
    )

    sw = page.get_by_role("switch", name="Demo toggle")
    expect(sw).to_have_count(1)
    expect(sw).to_have_attribute("aria-checked", "false")

    sw.focus()
    assert page.evaluate(
        "() => document.activeElement === document.querySelector('[role=switch]')"
    ), "the switch must be focusable"

    page.keyboard.press("Space")
    expect(sw).to_have_attribute("aria-checked", "true")


@pytest.mark.component
def test_modal_is_a_dialog_with_focus_moved_in(page: Page, base_url: str) -> None:
    """WCAG 4.1.2 / 2.4.3: a Modal must announce as a named dialog, move focus inside on open,
    and close (restoring focus) on Escape. Covers every modal flow (settings, project CRUD,
    move-chat, new-memory) through the one shared primitive."""
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { Modal } = await import('/components/ui/modal.js');
            Modal({ title: 'Settings', body: 'Hello' });
        }"""
    )

    dlg = page.get_by_role("dialog", name="Settings")
    expect(dlg).to_be_visible()
    expect(dlg).to_have_attribute("aria-modal", "true")

    # focus moved into the dialog (its first control), not stranded on <body>.
    assert page.evaluate(
        "() => !!document.activeElement && document.activeElement.closest('.modal') !== null"
    ), "focus must move into the open dialog"

    page.keyboard.press("Escape")
    expect(page.get_by_role("dialog")).to_have_count(0)


@pytest.mark.component
def test_icon_is_decorative_by_default_and_named_on_demand(page: Page, base_url: str) -> None:
    """WCAG 1.1.1: Icon() is the root of ~56 call sites. A bare icon must be hidden from AT
    (decorative); a genuinely standalone icon opts in via `label` and becomes a named image."""
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { Icon } = await import('/icons/icons.js');
            const wrap = document.createElement('div');
            wrap.append(Icon('send'));
            wrap.append(Icon('shield', { label: 'Admin' }));
            window.__mount(wrap);
        }"""
    )

    deco = page.locator("svg").first
    expect(deco).to_have_attribute("aria-hidden", "true")
    expect(deco).to_have_attribute("focusable", "false")

    # the opt-in meaningful icon is exposed as a named image instead.
    expect(page.get_by_role("img", name="Admin")).to_have_count(1)


def test_main_landmark_skip_link_and_route_title(page: Page, base_url: str) -> None:
    """WCAG 2.4.1 / 1.3.1 / 2.4.2: the app must expose a <main id=main> landmark, a skip link
    that targets it, and a title that describes the current view (not the bare brand)."""
    app = AppPage(page)
    app.goto(base_url, "/")
    app.wait_ready()

    expect(page.locator("main#main")).to_have_count(1)
    expect(page.locator("a.skip-link[href='#main']")).to_have_count(1)
    # the home route titles itself ("New chat - Gert"), not the static fallback "Gert".
    expect(page).to_have_title(re.compile(r"\s-\sGert$"))


def test_toast_host_is_a_live_region(page: Page, base_url: str) -> None:
    """WCAG 4.1.3: toasts are the single feedback channel for every user action (save, delete,
    upload, rename). The host must be a polite live region so they are announced."""
    app = AppPage(page)
    app.goto(base_url, "/")
    app.wait_ready()

    page.evaluate(
        """async () => {
            const { toast } = await import('/components/ui/toast.js');
            toast('Saved', 'ok');
        }"""
    )

    host = page.locator(".toast-host")
    expect(host).to_have_attribute("role", "status")
    expect(host).to_have_attribute("aria-live", "polite")
