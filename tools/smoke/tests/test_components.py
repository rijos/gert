"""test_components.py — VanJS component units via page.evaluate (testing.md §8).

A VanJS component is a function returning a real DOM node whose reactivity needs a
real DOM, so we mount the ACTUAL, unmocked module in the browser and assert. The
Fake host serves ``tests/web/harness.html`` at ``/tests/harness.html`` (import map
+ a ``__mount`` helper) on the same origin so ``/components/...`` and ``/state/...``
imports resolve.

Browser test: needs ``playwright install``. Caveats from §8: reuse one context
(see conftest), and VanJS batches DOM updates on a microtask — ``await`` a tick
before asserting.
"""

from __future__ import annotations

import re

import pytest
from playwright.sync_api import Page, expect

# Deterministic harness-mount tests (no LLM/backend round-trip) — part of the CI
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
    # Attach + Tools + the Thinking toggle (reasoning on/off, composer.js).
    expect(page.locator(".composer button.cbtn")).to_have_count(3)
    expect(page.locator(".composer button.cbtn").nth(2)).to_contain_text("Thinking")


def test_tool_card_expands(page: Page, base_url: str) -> None:
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { ToolCard } = await import('/components/main/tool-card.js');
            const { reactive } = await import('van-x');
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
            const { Citation } = await import('/components/main/message.js');
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
            const { reactive } = await import('van-x');
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


def test_tool_card_renders_stdout(page: Page, base_url: str) -> None:
    # The pre block a sandbox/clock card renders verbatim (tool_result.stdout).
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { ToolCard } = await import('/components/main/tool-card.js');
            const { reactive } = await import('van-x');
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


def test_tool_card_error_state(page: Page, base_url: str) -> None:
    # A refused/failed tool call styles the card as an error (the entitlement
    # ceiling surfaces visibly instead of pretending the tool ran).
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { ToolCard } = await import('/components/main/tool-card.js');
            const { reactive } = await import('van-x');
            const card = reactive({
                kind: 'todo', status: 'error', label: 'Updating the todo list',
                tag: 'todo', query: '', hits: [], stdout: '', todos: [], open: false,
            });
            window.__mount(ToolCard(card));
        }"""
    )
    expect(page.locator(".tcard")).to_have_class(re.compile(r"\berr\b"))
