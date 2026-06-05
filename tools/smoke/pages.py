"""pages.py — page objects for the Gert SPA regions.

Thin wrappers over Playwright locators that target the REAL selectors/classes in
``src/Gert.Api/wwwroot`` (the mockup class names: ``.composer``, ``.convo``,
``.tcard``, ``.kb-view``, ``.ctab``, ``.art-doc``, etc.). Keeping the selectors in
one place means a markup rename is a one-file edit, not a scatter across scenarios.

These are intentionally behaviour-light: they expose locators + a few actions; the
scenarios in ``tests/`` and ``run.py`` drive the assertions.
"""

from __future__ import annotations

from playwright.sync_api import Locator, Page


class Sidebar:
    """The left column: brand, project picker, new-chat, conversation list, user chip."""

    def __init__(self, page: Page) -> None:
        self.page = page
        self.root = page.locator(".sidebar")

    @property
    def new_chat(self) -> Locator:
        return self.root.locator(".new-chat, [class*='new-chat']").first

    @property
    def convos(self) -> Locator:
        return self.root.locator(".convo")

    def convo(self, title: str) -> Locator:
        return self.root.locator(".convo", has_text=title)

    @property
    def active_convo(self) -> Locator:
        return self.root.locator(".convo.active")

    @property
    def user_name(self) -> Locator:
        return self.root.locator(".userchip .name")

    @property
    def user_auth_line(self) -> Locator:
        return self.root.locator(".userchip .auth")


class Composer:
    """The message composer: textarea, attach, use-docs toggle, send."""

    def __init__(self, page: Page) -> None:
        self.page = page
        self.root = page.locator(".composer")

    @property
    def textarea(self) -> Locator:
        return self.root.locator("textarea")

    @property
    def send_button(self) -> Locator:
        return self.root.locator("button.send")

    @property
    def attach_button(self) -> Locator:
        return self.root.locator("button.cbtn", has_text="Attach")

    @property
    def use_docs_toggle(self) -> Locator:
        return self.root.locator("button.cbtn.toggle")

    @property
    def file_input(self) -> Locator:
        return self.root.locator("input[type=file]")

    def type(self, text: str) -> None:
        self.textarea.fill(text)

    def send(self, text: str) -> None:
        self.textarea.fill(text)
        self.send_button.click()


class Thread:
    """The scrolling message stream: messages, tool cards, citations, footnotes."""

    def __init__(self, page: Page) -> None:
        self.page = page
        self.root = page.locator(".stream")

    @property
    def messages(self) -> Locator:
        return self.root.locator(".msg")

    @property
    def bot_messages(self) -> Locator:
        return self.root.locator(".msg.bot")

    @property
    def user_messages(self) -> Locator:
        return self.root.locator(".msg.user")

    @property
    def last_bot_body(self) -> Locator:
        return self.bot_messages.last.locator(".body")

    @property
    def tool_cards(self) -> Locator:
        return self.root.locator(".tcard")

    @property
    def citations(self) -> Locator:
        return self.root.locator(".cite")

    @property
    def footnotes(self) -> Locator:
        # the sources card replaced the flat .footnotes list
        return self.root.locator(".sources, .footnotes, [class*='footnote']")

    def tool_card(self, label: str) -> Locator:
        return self.root.locator(".tcard", has_text=label)

    def expand_tool_card(self, card: Locator) -> None:
        card.locator(".thead").click()


class Knowledge:
    """The knowledge panel: doc list, status pills, drop zone, use-in-chat switch."""

    def __init__(self, page: Page) -> None:
        self.page = page
        self.root = page.locator(".kb-view")

    def open(self) -> None:
        """Show the knowledge view (click the canvas bar's KB button)."""
        self.page.locator('.kbtn[title="Knowledge base"]').click()
        self.root.wait_for(state="visible")

    @property
    def docs(self) -> Locator:
        return self.root.locator(".doc")

    @property
    def drop_zone(self) -> Locator:
        return self.root.locator(".dropzone, [class*='drop']").first

    @property
    def file_input(self) -> Locator:
        # Two hidden file inputs exist (composer + knowledge drop-zone); uploads
        # route through the composer's, so scope to it to stay unambiguous.
        return self.page.locator(".composer input[type=file]")

    def doc(self, name: str) -> Locator:
        return self.root.locator(".doc", has_text=name)

    def doc_pill(self, name: str) -> Locator:
        return self.doc(name).locator(".pill, [class*='pill']")

    @property
    def use_in_chat_switch(self) -> Locator:
        return self.root.locator(".usein .switch, .usein [class*='switch']").first


class Canvas:
    """The right canvas: artifact tabs, the active artifact, rendered/source mode."""

    def __init__(self, page: Page) -> None:
        self.page = page
        self.root = page.locator(".canvas, [class*='canvas']").first

    @property
    def tabs(self) -> Locator:
        return self.page.locator(".ctabs .ctab")

    def tab(self, kind: str) -> Locator:
        return self.page.locator(f".ctab[data-tab='{kind}']")

    @property
    def active_artifact(self) -> Locator:
        return self.page.locator(".art-doc.active")

    @property
    def html_iframe(self) -> Locator:
        return self.page.locator(".art-doc[data-type='html'] iframe")

    @property
    def problems_panel(self) -> Locator:
        return self.page.locator(".art-doc[data-type='py'] .problems")

    def set_mode_button(self, label: str) -> Locator:
        # Rendered/Source/Preview live in the artifact head as a seg-toggle.
        return self.active_artifact.locator("button", has_text=label)


class ModelPicker:
    def __init__(self, page: Page) -> None:
        self.page = page
        self.trigger = page.locator("button.model-btn")

    @property
    def menu_items(self) -> Locator:
        return self.page.locator(".model-picker .m-item")

    def open(self) -> None:
        self.trigger.click()

    def select(self, name: str) -> None:
        self.open()
        self.page.locator(".model-picker .m-item", has_text=name).first.click()

    @property
    def current_name(self) -> Locator:
        return self.trigger.locator(".mname")


class Chrome:
    """Top-level chrome: theme toggle, responsive drawers."""

    def __init__(self, page: Page) -> None:
        self.page = page
        self.theme_toggle = page.locator("button.theme-toggle")
        self.app = page.locator(".app")

    def current_theme(self) -> str | None:
        theme: str | None = self.page.evaluate(
            "() => document.documentElement.getAttribute('data-theme')"
        )
        return theme

    def toggle_theme(self) -> None:
        self.theme_toggle.click()


class AppPage:
    """Aggregate page object: one entry point exposing all regions."""

    def __init__(self, page: Page, base_url: str = "") -> None:
        self.page = page
        self.base_url = base_url
        self.sidebar = Sidebar(page)
        self.composer = Composer(page)
        self.thread = Thread(page)
        self.knowledge = Knowledge(page)
        self.canvas = Canvas(page)
        self.model_picker = ModelPicker(page)
        self.chrome = Chrome(page)

    def goto(self, base_url: str, path: str = "/") -> None:
        self.page.goto(f"{base_url.rstrip('/')}{path}")

    def wait_ready(self) -> None:
        # The shell mounts after auth resolves; .app appears then.
        self.page.locator(".app").wait_for(state="visible")
