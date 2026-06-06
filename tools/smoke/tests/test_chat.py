"""test_chat.py — new chat → send → streaming → tool cards → citations (§9).

Full-app E2E against the running FakeE2E host: the real SPA, real adapters → the
Python vLLM mock. The fixtures key replies off the last user message, so these
strings drive deterministic streamed responses.
"""

from __future__ import annotations

from playwright.sync_api import FilePayload, Page, expect

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


def test_reload_restores_persisted_thread(page: Page, base_url: str) -> None:
    # Send a turn, then re-open the conversation by URL with fresh SPA state.
    # This drives conversations.open() → GET .../conversations/{id}, whose flat
    # contract (id/title/messages[].text, string role) the SPA consumes directly.
    # Regression guard for the "loading earlier chats shows nothing / every
    # message labelled You" bug.
    app = _open(page, base_url)
    app.composer.send("hello")
    expect(app.thread.last_bot_body).to_contain_text("How can I help", timeout=15000)

    cid = page.evaluate("async () => (await import('/state/chat.js')).activeId.val")
    assert cid, "sending should have set an active conversation id"

    app.goto(base_url, f"/c/{cid}")
    app.wait_ready()
    # Re-fetched + normalised: the user turn and the assistant turn both come
    # back, the assistant as a .msg.bot (role mapped 1 → "assistant") with text.
    expect(app.thread.user_messages.last).to_contain_text("hello", timeout=15000)
    expect(app.thread.bot_messages.last).to_contain_text(
        "How can I help", timeout=15000
    )


def test_reload_during_generation_resumes(page: Page, base_url: str) -> None:
    # THE headline of the detached turn pipeline (chat-and-tools.md § detached
    # turns): generation survives the client. The slow fixture (delay_ms in
    # fixtures.json) paces the mock so we can reload MID-stream; the worker keeps
    # generating server-side, the thread GET returns the assistant row as
    # status=streaming, and conversations.open() resubscribes from its seq — the
    # bubble replays what was missed and finishes live.
    app = _open(page, base_url)
    app.composer.send("count slowly")

    # Mid-stream: the first words arrived, the turn is not done.
    expect(app.thread.last_bot_body).to_contain_text("one", timeout=15000)

    cid = page.evaluate("async () => (await import('/state/chat.js')).activeId.val")
    assert cid, "sending should have set an active conversation id"

    # Reload while the worker is still generating (fresh SPA state, same thread).
    app.goto(base_url, f"/c/{cid}")
    app.wait_ready()

    # The resumed bubble rebuilds the full text — including the tail produced
    # while no client was connected.
    expect(app.thread.bot_messages.last).to_contain_text(
        "six — done counting.", timeout=20000
    )


def test_new_conversation_appears_in_sidebar_without_reload(
    page: Page, base_url: str
) -> None:
    # Regression guard: sending the first message of a new chat materialises the
    # conversation server-side (create-if-missing on /messages). The sidebar must
    # show the new thread immediately — previously it only appeared after a reload
    # re-listed conversations.
    app = _open(page, base_url)
    app.composer.send("hello")
    expect(app.thread.last_bot_body).to_contain_text("How can I help", timeout=15000)

    cid = page.evaluate("async () => (await import('/state/chat.js')).activeId.val")
    assert cid, "sending should have set an active conversation id"

    # No goto/reload — the row should be in the reactive sidebar list already.
    expect(page.locator(f'.convo[data-id="{cid}"]')).to_have_count(1, timeout=10000)


def test_switch_conversations_mid_stream_keeps_both_chats(
    page: Page, base_url: str
) -> None:
    # Regression guard: starting a NEW chat and clicking an older conversation
    # mid-stream used to lose the new chat — it wasn't in the sidebar yet (the
    # list only refreshed at turn end) and the still-attached consumer kept the
    # streaming flag pinned, locking the composer on the older thread. Now the
    # sidebar lists the new thread on POST-accept, switching detaches the client
    # (the server turn keeps running), and switching back re-attaches.
    app = _open(page, base_url)

    # Conversation A, completed.
    app.composer.send("hello")
    expect(app.thread.last_bot_body).to_contain_text("How can I help", timeout=15000)
    cid_a = page.evaluate("async () => (await import('/state/chat.js')).activeId.val")
    assert cid_a

    # New chat B: the slow fixture (6 deltas x 500ms) keeps the turn streaming
    # while we click around.
    app.sidebar.new_chat.click()
    app.composer.send("count slowly")
    expect(app.thread.user_messages.last).to_contain_text("count slowly")
    cid_b = page.evaluate("async () => (await import('/state/chat.js')).activeId.val")
    assert cid_b and cid_b != cid_a

    # Mid-stream: B is ALREADY in the sidebar (no reload, turn not finished).
    expect(page.locator(f'.convo[data-id="{cid_b}"]')).to_have_count(1, timeout=10000)

    # Switch to A mid-stream — A's thread renders…
    page.locator(f'.convo[data-id="{cid_a}"]').click()
    expect(app.thread.bot_messages.last).to_contain_text(
        "How can I help", timeout=10000
    )
    # …and the composer is UNLOCKED for A: a send round-trips (previously the
    # pinned streaming flag silently swallowed it).
    app.composer.send("hello again")
    expect(app.thread.user_messages.last).to_contain_text("hello again", timeout=10000)
    expect(app.thread.bot_messages).to_have_count(2, timeout=15000)

    # Switch back to B: the thread re-attaches (or reads the finished row) and
    # carries the full slow-count text — nothing was lost while detached.
    page.locator(f'.convo[data-id="{cid_b}"]').click()
    expect(app.thread.bot_messages.last).to_contain_text(
        "six — done counting.", timeout=20000
    )
    # B's turn ran with B detached part of the time — its bubble must not have
    # leaked into A. A reopen of A shows exactly its two exchanges.
    page.locator(f'.convo[data-id="{cid_a}"]').click()
    expect(app.thread.user_messages).to_have_count(2, timeout=10000)
    expect(app.thread.bot_messages.last).to_contain_text("Echo: hello again")


def test_delete_conversation_from_sidebar(page: Page, base_url: str) -> None:
    # The per-row trash button removes a conversation. Send a turn so a row
    # exists, reload so the sidebar lists it, then hover + click its trash.
    app = _open(page, base_url)
    app.composer.send("hello")
    expect(app.thread.last_bot_body).to_contain_text("How can I help", timeout=15000)

    cid = page.evaluate("async () => (await import('/state/chat.js')).activeId.val")
    assert cid, "sending should have set an active conversation id"

    app.goto(base_url, "/")  # boot re-lists conversations into the sidebar
    app.wait_ready()
    row = page.locator(f'.convo[data-id="{cid}"]')
    expect(row).to_have_count(1, timeout=10000)

    row.hover()
    row.locator(".trash").click()
    # The row leaves the reactive list once the DELETE resolves.
    expect(page.locator(f'.convo[data-id="{cid}"]')).to_have_count(0, timeout=10000)


def test_tool_call_renders_tool_card(page: Page, base_url: str) -> None:
    app = _open(page, base_url)
    app.composer.send("search my docs about qdrant")
    # The rag tool fixture emits a tool call → a .tcard renders, then final text.
    expect(app.thread.tool_cards.first).to_be_visible(timeout=15000)
    expect(app.thread.last_bot_body).to_contain_text("sqlite-vec wins", timeout=15000)


def test_citations_render_as_chips(page: Page, base_url: str) -> None:
    app = _open(page, base_url)
    # Precondition: a retrievable document. The rag tool only emits a citation
    # when its hybrid search returns a hit — without an ingested doc the canned
    # "[1]" has nothing to bind to. Hybrid search matches the keyword "qdrant",
    # so this doc is returned, the orchestrator emits a citation, and the "[1]"
    # marker becomes a .cite chip.
    payload: FilePayload = {
        "name": "qdrant-vs-sqlite-vec.txt",
        "mimeType": "text/plain",
        "buffer": b"Qdrant vs sqlite-vec: for a homelab, sqlite-vec wins on simplicity.",
    }
    app.knowledge.file_input.set_input_files(files=[payload])
    # Wait for ingestion to finish (status pill / poll is driven by the SPA's
    # documents service; the panel itself may be closed, so poll the service).
    page.wait_for_function(
        """async () => {
            const svc = await import('/services/documents.js');
            const docs = await svc.list();
            return Array.isArray(docs)
                && docs.some((d) => d.name
                    && d.name.includes('qdrant')
                    && d.status === 'ready');
        }""",
        timeout=45000,
    )

    app.composer.send("search my docs about qdrant")
    expect(app.thread.last_bot_body).to_contain_text("wins", timeout=15000)
    expect(app.thread.citations.first).to_be_visible(timeout=15000)
