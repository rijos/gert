"""test_llm_tools.py — artifacts, memory retrieval, todos, and the clock through
the FULL stack (§9): real SPA → real adapters → the Python vLLM mock, with
fixtures.json keying deterministic tool calls off the last user message.

Covers the four flows the scenario matrix smoke-checks, with deeper assertions:
the artifact persists across a reload, the memory hit carries the DECODED title
plus a citation chip, the todo checklist renders per-status, the clock card shows
a wall-clock reading, and the tool-entitlement ceiling errors the card without
killing the turn.
"""

from __future__ import annotations

import re

from playwright.sync_api import Page, expect

from tools.smoke.pages import AppPage


def _open(page: Page, base_url: str) -> AppPage:
    app = AppPage(page)
    app.goto(base_url, "/")
    app.wait_ready()
    return app


# ---- artifacts ---------------------------------------------------------------


def test_named_fence_opens_a_canvas_artifact(page: Page, base_url: str) -> None:
    app = _open(page, base_url)
    app.composer.send("make me a demo html page")

    # The artifact event opens a canvas tab named after the fence…
    tab = app.canvas.tab("html")
    expect(tab).to_be_visible(timeout=15000)
    expect(tab).to_contain_text("demo.html")

    # …whose sandboxed iframe renders the fence body (F3: no allow-same-origin).
    frame = app.canvas.html_iframe
    expect(frame).to_be_visible(timeout=15000)
    sandbox = frame.get_attribute("sandbox") or ""
    assert "allow-same-origin" not in sandbox
    expect(frame.content_frame.locator("h1")).to_contain_text("Demo", timeout=15000)

    # The fence also stays inline in the bubble (extraction is additive).
    expect(app.thread.last_bot_body).to_contain_text(
        "I opened it in the canvas.", timeout=15000
    )


def test_artifact_persists_across_reload(page: Page, base_url: str) -> None:
    # InsertArtifactAsync persisted the row; the thread GET returns it, so a
    # fresh SPA boot of the same conversation rebuilds the canvas tab.
    app = _open(page, base_url)
    app.composer.send("make me a demo html page")
    expect(app.canvas.tab("html")).to_be_visible(timeout=15000)
    expect(app.thread.last_bot_body).to_contain_text(
        "I opened it in the canvas.", timeout=15000
    )

    cid = page.evaluate("async () => (await import('/state/chat.js')).activeId.val")
    assert cid, "sending should have set an active conversation id"

    app.goto(base_url, f"/c/{cid}")
    app.wait_ready()
    expect(app.canvas.tab("html")).to_be_visible(timeout=15000)
    expect(app.canvas.tab("html")).to_contain_text("demo.html")


# ---- memory ------------------------------------------------------------------


def test_memory_entry_is_retrieved_with_decoded_title(
    page: Page, base_url: str
) -> None:
    app = _open(page, base_url)

    # Seed a memory entry through the SPA's own service (bearer + project
    # scoping ride along); MemoryService embeds it into rag.db as kind='memory'.
    page.evaluate(
        """async () => {
            const memory = await import('/services/memory.js');
            await memory.add({
                title: 'Favorite database',
                content: 'My favorite database is sqlite-vec, running the homelab RAG stack.',
            });
        }"""
    )

    # The fixture fires search_documents("favorite database") — the memory entry
    # rides the same hybrid query as documents.
    app.composer.send("search my docs about favorite database")
    card = app.thread.tool_cards.first
    expect(card).to_be_visible(timeout=15000)
    app.thread.expand_tool_card(card)

    # The hit shows the DECODED title (documents.filename holds it base64-encoded).
    hit = card.locator(".doc-hit").first
    expect(hit).to_contain_text("Favorite database", timeout=15000)
    expect(hit).not_to_contain_text("RmF2b3JpdGU")  # base64("Favorite…") prefix

    # The citation binds the bubble's [1] marker to the memory entry.
    expect(app.thread.last_bot_body).to_contain_text(
        "sqlite-vec is your favorite", timeout=15000
    )
    expect(app.thread.citations.first).to_be_visible(timeout=15000)


# ---- todos -------------------------------------------------------------------


def test_todo_checklist_renders_with_statuses(page: Page, base_url: str) -> None:
    app = _open(page, base_url)
    app.composer.send("plan the homelab upgrade")

    # The todo card auto-opens with the model's checklist.
    rows = app.thread.todo_rows
    expect(rows).to_have_count(3, timeout=15000)
    expect(rows.nth(0)).to_have_class(re.compile(r"\bdone\b"))
    expect(rows.nth(0)).to_contain_text("Order the new SSD")
    expect(rows.nth(1)).to_have_class(re.compile(r"\bactive\b"))
    expect(rows.nth(1)).to_contain_text("Migrate rag.db to the new disk")
    expect(rows.nth(2)).to_have_class(re.compile(r"\bpending\b"))
    expect(rows.nth(2)).to_contain_text("Re-embed the document corpus")

    # The turn finishes normally after the tool round.
    expect(app.thread.last_bot_body).to_contain_text("Plan is up", timeout=15000)


def test_todo_tool_is_refused_without_the_entitlement(
    user_page: Page, base_url: str
) -> None:
    # `user` carries gert_tools "rag search" — set_todos is refused at execution
    # time (the claim is the ceiling), the card errors, the turn still completes.
    app = _open(user_page, base_url)
    app.composer.send("plan the homelab upgrade")

    expect(app.thread.errored_tool_cards.first).to_be_visible(timeout=15000)
    expect(app.thread.todo_rows).to_have_count(0)
    expect(app.thread.last_bot_body).to_contain_text("Plan is up", timeout=15000)


# ---- clock -------------------------------------------------------------------


def test_clock_card_shows_a_wall_clock_reading(page: Page, base_url: str) -> None:
    app = _open(page, base_url)
    app.composer.send("what time is it")

    card = app.thread.tool_cards.first
    expect(card).to_be_visible(timeout=15000)
    app.thread.expand_tool_card(card)

    # The reading comes from the host's TimeProvider — assert shape, not instant.
    expect(app.thread.tool_stdout.first).to_contain_text(
        re.compile(r"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \(UTC, \w+\)"), timeout=15000
    )
    expect(app.thread.last_bot_body).to_contain_text("on the card above", timeout=15000)
