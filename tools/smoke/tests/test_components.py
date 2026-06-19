"""test_components.py - VanJS component units via page.evaluate (testing.md section 8).

A VanJS component is a function returning a real DOM node whose reactivity needs a
real DOM, so we mount the ACTUAL, unmocked module in the browser and assert. The
Fake host serves ``tests/web/harness.html`` at ``/tests/harness.html`` (a
``__mount`` helper) on the same origin so ``/components/...`` and ``/state/...``
imports resolve (absolute same-origin paths, no import map).

Browser test: needs ``playwright install``. Caveats from section 8: reuse one context
(see conftest), and VanJS batches DOM updates on a microtask - ``await`` a tick
before asserting.
"""

from __future__ import annotations

import re

import pytest
from playwright.sync_api import Page, expect

# Deterministic harness-mount tests (no LLM/backend round-trip) - part of the CI
# gate via `run.py --pytest -m component`.
pytestmark = pytest.mark.component


def test_convo_item_active(page: Page, base_url: str) -> None:
    page.goto(f"{base_url}/tests/harness.html")
    cls = page.evaluate(
        """async () => {
            const { ConvoItem } = await import('/components/sidebar/convo-item.js');
            const chat = await import('/state/chat.js');
            const node = ConvoItem({ id: 'c1', title: 'Hello' });
            document.body.append(node);
            chat.activeId.val = 'c1';
            await new Promise(r => setTimeout(r));   // let van flush batched updates
            return node.className;
        }"""
    )
    assert "active" in cls


def test_composer_renders_send_and_attach(page: Page, base_url: str) -> None:
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { Composer } = await import('/components/main/composer.js');
            window.__mount(Composer());
        }"""
    )
    expect(page.locator(".composer textarea")).to_be_visible()
    expect(page.locator(".composer button.send")).to_be_visible()
    # Attach + Tools only - thinking is a provider preset in the model picker,
    # not a per-conversation composer toggle.
    expect(page.locator(".composer button.cbtn")).to_have_count(2)


def test_tool_card_expands(page: Page, base_url: str) -> None:
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { ToolCard } = await import('/components/main/tool-card.js');
            const { reactive } = await import('/lib/van-x.js');
            // a van-x reactive tool entry, exactly the shape state/chat.js pushes,
            // so card.open toggling drives a real re-render.
            const card = reactive({
                kind: 'rag', status: 'done', label: 'Retrieving', tag: 'rag',
                query: 'qdrant', hits: [], stdout: '', open: false,
            });
            window.__mount(ToolCard(card));
        }"""
    )
    card = page.locator(".tcard")
    expect(card).to_be_visible()
    expect(card.locator(".tbody")).to_have_class("tbody hide")
    card.locator(".thead").click()
    page.wait_for_timeout(50)
    expect(card.locator(".tbody")).not_to_have_class("tbody hide")


def test_citation_chip(page: Page, base_url: str) -> None:
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { Citation } = await import('/components/main/citation.js');
            window.__mount(Citation({ ordinal: 3, label: 'My doc' }));
        }"""
    )
    chip = page.locator(".cite")
    expect(chip).to_have_text("3")
    expect(chip).to_have_attribute("title", "My doc")


def test_tool_card_renders_todo_checklist(page: Page, base_url: str) -> None:
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { ToolCard } = await import('/components/main/tool-card.js');
            const { reactive } = await import('/lib/van-x.js');
            const card = reactive({
                kind: 'todo', status: 'done', label: 'Updating the todo list',
                tag: 'todo', query: '', hits: [], stdout: '', open: true,
                todos: [
                    { text: 'Order the new SSD', status: 'done' },
                    { text: 'Migrate rag.db', status: 'active' },
                    { text: 'Re-embed the corpus', status: 'pending' },
                ],
            });
            window.__mount(ToolCard(card));
        }"""
    )
    rows = page.locator(".tcard .todo-row")
    expect(rows).to_have_count(3)
    # Per-status classes drive the marker + strikethrough styling.
    expect(rows.nth(0)).to_have_class("todo-row done")
    expect(rows.nth(1)).to_have_class("todo-row active")
    expect(rows.nth(2)).to_have_class("todo-row pending")
    expect(rows.nth(1)).to_contain_text("Migrate rag.db")
    # The header count + progress bar reflect done/total (1 of 3) and live
    # OUTSIDE the collapsible body, so they stay visible when collapsed.
    expect(page.locator(".tcard .tcount")).to_have_text("1/3")
    bar = page.locator(".tcard .pbar")
    expect(bar).to_have_attribute("aria-valuenow", "1")
    expect(bar).to_have_attribute("aria-valuemax", "3")


def test_todo_card_progress_tracks_status_changes(page: Page, base_url: str) -> None:
    # Checking off the remaining steps drives the bar to 100% reactively.
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { ToolCard } = await import('/components/main/tool-card.js');
            const { reactive } = await import('/lib/van-x.js');
            const card = reactive({
                kind: 'todo', status: 'done', label: 'Updating the todo list',
                tag: 'todo', query: '', hits: [], stdout: '', open: true,
                todos: [
                    { text: 'Order the new SSD', status: 'done' },
                    { text: 'Migrate rag.db', status: 'active' },
                ],
            });
            window.__mount(ToolCard(card));
            window.__card = card;
        }"""
    )
    expect(page.locator(".tcard .tcount")).to_have_text("1/2")
    page.evaluate(
        """async () => {
            window.__card.todos = [
                { text: 'Order the new SSD', status: 'done' },
                { text: 'Migrate rag.db', status: 'done' },
            ];
            await new Promise(r => setTimeout(r));   // let van flush batched updates
        }"""
    )
    expect(page.locator(".tcard .tcount")).to_have_text("2/2")
    bar = page.locator(".tcard .pbar")
    expect(bar).to_have_attribute("aria-valuenow", "2")
    expect(bar.locator("i")).to_have_attribute("style", re.compile(r"width:\s*100"))


def test_progress_bar_renders_value(page: Page, base_url: str) -> None:
    # The shared ui/progress-bar.js: ARIA contract + width % from value/max.
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { ProgressBar } = await import('/components/ui/progress-bar.js');
            window.__mount(ProgressBar({ value: 2, max: 4 }));
        }"""
    )
    bar = page.locator(".pbar")
    expect(bar).to_have_attribute("role", "progressbar")
    expect(bar).to_have_attribute("aria-valuenow", "2")
    expect(bar).to_have_attribute("aria-valuemax", "4")
    expect(bar.locator("i")).to_have_attribute(
        "style", re.compile(r"width:\s*50(\.0)?%")
    )


def test_tool_card_renders_stdout(page: Page, base_url: str) -> None:
    # The pre block a sandbox/clock card renders verbatim (tool_result.stdout).
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { ToolCard } = await import('/components/main/tool-card.js');
            const { reactive } = await import('/lib/van-x.js');
            const card = reactive({
                kind: 'clock', status: 'done', label: 'Checking the date & time',
                tag: 'clock', query: '', hits: [], todos: [], open: true,
                stdout: '2026-06-05 12:30:00 (UTC, Friday)',
            });
            window.__mount(ToolCard(card));
        }"""
    )
    expect(page.locator(".tcard .stdout")).to_have_text(
        "2026-06-05 12:30:00 (UTC, Friday)"
    )


def test_markdown_renders_gfm_table(page: Page, base_url: str) -> None:
    # GFM tables (the shape qwen likes for summaries) become real <table> DOM -
    # not a pipe-soup paragraph. Cells go through inline(), alignment through
    # the delimiter row; the no-raw-HTML stance holds (markdown.js).
    page.goto(f"{base_url}/tests/harness.html")
    result = page.evaluate(
        """async () => {
            const { renderMarkdown } = await import('/lib/markdown.js');
            const host = document.createElement('div');
            host.append(renderMarkdown([
                'A quick summary:',
                '| Script | What it does | Size |',
                '|---|:---:|---:|',
                '| `file_organizer.py` | Sorts files | 12kb |',
                '| <b>bold?</b> | **really** | 3kb |',
            ].join('\\n')));
            return {
                tables: host.querySelectorAll('table').length,
                ths: [...host.querySelectorAll('th')].map(n => n.textContent),
                rows: host.querySelectorAll('tbody tr').length,
                codeCells: host.querySelectorAll('td code').length,
                strongCells: host.querySelectorAll('td strong').length,
                rawHtml: host.querySelectorAll('td b').length,
                center: host.querySelector('tbody td:nth-child(2)').style.textAlign,
                right: host.querySelector('tbody td:nth-child(3)').style.textAlign,
                paragraph: host.querySelector('p')?.textContent,
            };
        }"""
    )
    assert result["tables"] == 1
    assert result["ths"] == ["Script", "What it does", "Size"]
    assert result["rows"] == 2
    assert result["codeCells"] == 1  # `file_organizer.py`
    assert result["strongCells"] == 1  # **really**
    assert result["rawHtml"] == 0  # <b> stays literal text (security F4)
    assert result["center"] == "center"
    assert result["right"] == "right"
    # the preceding paragraph didn't swallow the table's header row
    assert result["paragraph"] == "A quick summary:"


def test_tool_card_error_state(page: Page, base_url: str) -> None:
    # A refused/failed tool call styles the card as an error (the entitlement
    # ceiling surfaces visibly instead of pretending the tool ran).
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { ToolCard } = await import('/components/main/tool-card.js');
            const { reactive } = await import('/lib/van-x.js');
            const card = reactive({
                kind: 'todo', status: 'error', label: 'Updating the todo list',
                tag: 'todo', query: '', hits: [], stdout: '', todos: [], open: false,
            });
            window.__mount(ToolCard(card));
        }"""
    )
    expect(page.locator(".tcard")).to_have_class(re.compile(r"\berr\b"))


def test_markdown_gallery_all_self_checks_pass(page: Page, base_url: str) -> None:
    # The markdown gallery renders a battery of CommonMark/GFM/math inputs through
    # the REAL lib/markdown.js (+ lib/smath.js -> native MathML) and self-checks
    # the F4 security stance, heading anchors, syntax highlight, and math in a real
    # browser - the coverage the headless unit tests can't give (native <math>
    # layout, the no-import-map CSP boot). It exposes a machine-readable verdict.
    page.goto(f"{base_url}/tests/markdown-gallery.html")
    page.wait_for_function("() => window.__galleryReady === true")
    result = page.evaluate("() => window.__galleryResult")

    # surface WHICH card failed (not just a bare False) for a fast diagnosis
    fails = page.evaluate(
        "() => [...document.querySelectorAll('.verdict.fail')].map(n => n.textContent)"
    )
    assert result["securityAllPass"] is True, f"security self-checks failed: {fails}"
    assert result["functionalAllPass"] is True, (
        f"functional self-checks failed: {fails}"
    )

    # counts guard against a check silently disappearing from the battery
    # (\ce/\color renderer support added: 1 feature card, 2 security cards - a
    # charset-validated colour + inert \ce HTML - and 3 functional cards.)
    assert result["featureCount"] == 28
    assert result["securityCount"] == 20
    assert result["functionalCount"] == 21

    # sanitizeUrl chokepoint (exported, used by callers elsewhere)
    s = result["sanitize"]
    assert s["js"] == "#"
    assert s["jsSpaced"] == "#"
    assert s["jsTab"] == "#"
    assert s["httpOk"] == "http://ok/path"
    assert s["rel"] == "/relative"
    assert s["anchor"] == "#a"
    assert s["protoRel"] == "//host"
