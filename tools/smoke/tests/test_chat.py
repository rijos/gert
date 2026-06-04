"""test_chat.py — new chat → send → streaming → tool cards → citations (§9).

Full-app E2E against the running FakeE2E host: the real SPA, real adapters → the
Python vLLM mock. The fixtures key replies off the last user message, so these
strings drive deterministic streamed responses.
"""

from __future__ import annotations

from playwright.sync_api import Page, expect

from tools.smoke.pages import AppPage


def _open(page: Page, base_url: str) -> AppPage:
    app = AppPage(page)
    app.goto(base_url, "/")
    app.wait_ready()
    return app


def test_send_streams_assistant_message(page: Page, base_url: str) -> None:
    app = _open(page, base_url)
    app.composer.send("hello")
    # The user bubble + an assistant bubble appear; the assistant streams content.
    expect(app.thread.user_messages.last).to_contain_text("hello")
    expect(app.thread.last_bot_body).to_contain_text("How can I help", timeout=15000)


def test_tool_call_renders_tool_card(page: Page, base_url: str) -> None:
    app = _open(page, base_url)
    app.composer.send("search my docs about qdrant")
    # The rag tool fixture emits a tool call → a .tcard renders, then final text.
    expect(app.thread.tool_cards.first).to_be_visible(timeout=15000)
    expect(app.thread.last_bot_body).to_contain_text("sqlite-vec wins", timeout=15000)


def test_citations_render_as_chips(page: Page, base_url: str) -> None:
    app = _open(page, base_url)
    app.composer.send("search my docs about qdrant")
    # The "[1]" in the final text is injected as a .cite chip when a citation exists.
    expect(app.thread.last_bot_body).to_contain_text("wins", timeout=15000)
    # Citation rendering depends on the orchestrator emitting a citation event;
    # assert the chip if present (the rag path emits one).
    expect(app.thread.citations.first).to_be_visible(timeout=15000)
