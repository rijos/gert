"""test_llm_tools.py - artifacts, todos, and the clock through the FULL stack
(section 9): real SPA -> real adapters -> the Python vLLM mock, with fixtures.json
keying deterministic tool calls off the last user message.

Covers the flows the scenario matrix smoke-checks, with deeper assertions:
the artifact persists across a reload, the todo checklist renders per-status, the
clock card shows a wall-clock reading, and the tool-entitlement ceiling errors the
card without killing the turn.
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


def test_make_artifact_opens_a_canvas_artifact(page: Page, base_url: str) -> None:
    app = _open(page, base_url)
    app.composer.send("make me a demo html page")

    # The make_artifact tool call opens a canvas tab named after the artifact...
    tab = app.canvas.tab("html")
    expect(tab).to_be_visible(timeout=15000)
    expect(tab).to_contain_text("demo.html")

    # ...whose sandboxed iframe renders the artifact content (F3: no allow-same-origin).
    frame = app.canvas.html_iframe
    expect(frame).to_be_visible(timeout=15000)
    sandbox = frame.get_attribute("sandbox") or ""
    assert "allow-same-origin" not in sandbox
    expect(frame.content_frame.locator("h1")).to_contain_text("Demo", timeout=15000)

    # The bubble carries only the bot's acknowledgement - the file content went out
    # as a tool argument, so (unlike the old fence model) it is NOT inline.
    expect(app.thread.last_bot_body).to_contain_text(
        "I opened demo.html in the canvas.", timeout=15000
    )


def test_html_artifact_renders_cross_origin_and_runs_scripts(
    page: Page, base_url: str
) -> None:
    """F3 served-document hardening: the HTML artifact is framed from the SEPARATE
    artifact origin (a distinct port in dev/CI), so it gets its own non-inherited
    CSP - which both (a) restores inline-script fidelity that srcdoc inheritance
    silently blocked, and (b) keeps it cross-origin + opaque from the app."""
    from urllib.parse import urlparse

    app = _open(page, base_url)
    app.composer.send("make me an interactive html page")

    expect(app.canvas.tab("html")).to_be_visible(timeout=15000)
    frame = app.canvas.html_iframe
    expect(frame).to_be_visible(timeout=15000)
    # F3: never same-origin-capable, regardless of how it's framed.
    assert "allow-same-origin" not in (frame.get_attribute("sandbox") or "")

    # Fidelity: the inline <script> ran and mutated the DOM. Under the old srcdoc
    # render the inherited app CSP (script-src 'self') blocked this outright.
    cf = frame.content_frame
    expect(cf.locator("#out")).to_have_text("SCRIPT-RAN", timeout=15000)

    # Served from the separate artifact origin (ticketed /raw), NOT the app origin.
    src = frame.get_attribute("src") or ""
    assert "/artifacts/raw?t=" in src, f"expected a ticketed raw URL, got {src!r}"
    assert urlparse(src).netloc != urlparse(base_url).netloc, (
        f"artifact must render cross-origin; app={base_url} frame={src}"
    )


def test_html_artifact_buttons_click_and_cannot_escape(
    page: Page, base_url: str
) -> None:
    """F3, both halves on the REAL served path: a plain button inside the
    sandboxed artifact works (its inline onclick mutates the artifact's own
    DOM), while every escape hatch - parent/top DOM, storage, cookies, network
    egress - reports blocked."""
    app = _open(page, base_url)
    app.composer.send("make me a clickable html page")

    expect(app.canvas.tab("html")).to_be_visible(timeout=15000)
    frame = app.canvas.html_iframe
    expect(frame).to_be_visible(timeout=15000)
    cf = frame.content_frame

    # The artifact's own script ran: the probes resolved...
    expect(cf.locator("#esc")).to_have_text(
        "parent-dom:blocked top-dom:blocked storage:blocked cookie:blocked",
        timeout=15000,
    )
    # ...and the CSP (no connect-src grant) killed the beacon.
    expect(cf.locator("#net")).to_have_text("net:blocked", timeout=15000)

    # Safe interactivity: clicking the button mutates the artifact's DOM.
    expect(cf.locator("#out")).to_have_text("idle")
    cf.locator("#btn").click()
    expect(cf.locator("#out")).to_have_text("clicked")


def test_sub_agent_delegates_and_returns_the_result(page: Page, base_url: str) -> None:
    """The sub-agent loop end-to-end: the model delegates a task via
    run_sub_agent, a FRESH nested completion answers it (the mock's echo
    fallback - the nested request matches no fixture), and only that final
    answer lands on the parent's single tool card."""
    app = _open(page, base_url)
    app.composer.send("delegate the research")

    app.thread.open_activity()
    card = app.thread.tool_cards.first
    expect(card).to_be_visible(timeout=15000)
    app.thread.expand_tool_card(card)
    # The nested conversation answered the task; its final text is the card output.
    expect(app.thread.tool_stdout.first).to_contain_text(
        "summarize the gert release notes", timeout=15000
    )
    expect(app.thread.last_bot_body).to_contain_text(
        "finished the research", timeout=15000
    )


def test_artifact_persists_across_reload(page: Page, base_url: str) -> None:
    # InsertArtifactAsync persisted the row; the thread GET returns it, so a
    # fresh SPA boot of the same conversation rebuilds the canvas tab.
    app = _open(page, base_url)
    app.composer.send("make me a demo html page")
    expect(app.canvas.tab("html")).to_be_visible(timeout=15000)
    expect(app.thread.last_bot_body).to_contain_text(
        "I opened demo.html in the canvas.", timeout=15000
    )

    cid = page.evaluate("async () => (await import('/state/chat.js')).activeId.val")
    assert cid, "sending should have set an active conversation id"

    app.goto(base_url, f"/c/{cid}")
    app.wait_ready()
    expect(app.canvas.tab("html")).to_be_visible(timeout=15000)
    expect(app.canvas.tab("html")).to_contain_text("demo.html")


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


def test_todo_card_collapses_when_all_steps_done(page: Page, base_url: str) -> None:
    """All steps done -> the single todo card auto-collapses to its summary row;
    the header count + progress bar stay visible, and the user can re-open it."""
    app = _open(page, base_url)
    app.composer.send("finish the homelab upgrade")
    app.thread.open_activity()

    # The live label reads "Updated todo list" once every box is checked, so
    # match the stable "todo list" stem.
    card = app.thread.tool_card("todo list")
    expect(card.locator(".tcount")).to_have_text("2/2", timeout=15000)
    # Auto-collapses (after a short beat) to the summary row; the bar shows
    # 100% (green fill) while closed.
    expect(card.locator(".tbody")).to_have_class("tbody hide", timeout=5000)
    expect(card).to_have_class(re.compile(r"\bcollapsed\b"))
    expect(card.locator(".tsummary")).to_contain_text("All 2 tasks complete")
    expect(card.locator(".lab")).to_have_text("Updated todo list")
    expect(card.locator(".pbar")).to_have_attribute("aria-valuenow", "2")
    expect(card.locator(".pbar i")).to_have_attribute(
        "style", re.compile(r"width:\s*100")
    )
    # The user is free to re-open it after the work (summary row click)...
    card.locator(".tsummary").click()
    expect(card.locator(".tbody")).not_to_have_class("tbody hide")
    expect(app.thread.todo_rows).to_have_count(2)
    # ...and collapse it again (header click) - back to the summary row.
    card.locator(".thead").click()
    expect(card.locator(".tbody")).to_have_class("tbody hide")
    expect(card.locator(".tsummary")).to_be_visible()

    expect(app.thread.last_bot_body).to_contain_text("All done", timeout=15000)


def test_todo_card_survives_reload(page: Page, base_url: str) -> None:
    """The checklist must come back on a thread GET - reconstructed from the
    persisted tool_calls row (ThreadToolCall -> toCards), not only the live
    tool_call/tool_result stream. Regression guard for the "todo card vanishes
    when you re-open the conversation" bug."""
    app = _open(page, base_url)
    app.composer.send("plan the homelab upgrade")
    expect(app.thread.todo_rows).to_have_count(3, timeout=15000)
    expect(app.thread.last_bot_body).to_contain_text("Plan is up", timeout=15000)

    cid = page.evaluate("async () => (await import('/state/chat.js')).activeId.val")
    assert cid, "sending should have set an active conversation id"

    # Fresh SPA state, same thread - the card is rebuilt from persistence.
    app.goto(base_url, f"/c/{cid}")
    app.wait_ready()

    # Mid-list the header reads "Now: <active step>" (todoLabel), so select the
    # card itself - there is exactly one on this thread.
    app.thread.open_activity()
    card = app.thread.tool_cards.first
    expect(card).to_be_visible(timeout=15000)
    expect(card.locator(".lab")).to_have_text("Now: Migrate rag.db to the new disk")
    # Rebuilt cards come back collapsed; the checklist is intact underneath.
    app.thread.expand_tool_card(card)
    rows = app.thread.todo_rows
    expect(rows).to_have_count(3)
    expect(rows.nth(0)).to_have_class(re.compile(r"\bdone\b"))
    expect(rows.nth(0)).to_contain_text("Order the new SSD")
    expect(rows.nth(1)).to_have_class(re.compile(r"\bactive\b"))
    expect(rows.nth(2)).to_have_class(re.compile(r"\bpending\b"))
    # The header count + progress survive too (1 of 3 done).
    expect(card.locator(".tcount")).to_have_text("1/3")


def test_todo_tool_is_refused_without_the_entitlement(
    user_page: Page, base_url: str
) -> None:
    # `user` carries gert_tools "rag search ask_user fetch sub_agent list_artifacts"
    # - it has no `todo` entitlement, so set_todos is dropped by the ceiling. The drop stays
    # silent toward the user (auth.md - the boundary does not leak into the
    # conversation): NO tool card surfaces, yet the turn still completes because the
    # model reads the synthetic refusal and answers around the dropped tool.
    app = _open(user_page, base_url)
    app.composer.send("plan the homelab upgrade")

    # The turn finishes (proving the refusal reached the model) with no visible
    # trace of the refused call - no errored card, no checklist.
    expect(app.thread.last_bot_body).to_contain_text("Plan is up", timeout=15000)
    expect(app.thread.tool_cards).to_have_count(0)
    expect(app.thread.todo_rows).to_have_count(0)


def test_run_python_card_shows_stdout(page: Page, base_url: str) -> None:
    """run_python runs on the default monty backend (through the monty mock) and the
    captured stdout lands verbatim on the tool card; the turn then completes. Proves
    the real MontySandbox adapter HTTP path end to end (admin holds gert_tools '*')."""
    app = _open(page, base_url)
    app.composer.send("run python to add two and two")

    app.thread.open_activity()
    card = app.thread.tool_cards.first
    expect(card).to_be_visible(timeout=15000)
    app.thread.expand_tool_card(card)

    # The mock evaluates print(2 + 2) -> "4"; MontySandbox carries it through the tool
    # loop to the card's verbatim stdout pre-block.
    expect(app.thread.tool_stdout.first).to_contain_text("4", timeout=15000)
    expect(app.thread.last_bot_body).to_contain_text("The result is 4", timeout=15000)


def test_clock_card_shows_a_wall_clock_reading(page: Page, base_url: str) -> None:
    app = _open(page, base_url)
    app.composer.send("what time is it")

    app.thread.open_activity()
    card = app.thread.tool_cards.first
    expect(card).to_be_visible(timeout=15000)
    app.thread.expand_tool_card(card)

    # The reading comes from the host's TimeProvider - assert shape, not instant.
    expect(app.thread.tool_stdout.first).to_contain_text(
        re.compile(r"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \(UTC, \w+\)"), timeout=15000
    )
    expect(app.thread.last_bot_body).to_contain_text("on the card above", timeout=15000)
