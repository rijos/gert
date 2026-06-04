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

from playwright.sync_api import Page, expect


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
    expect(page.locator(".composer button.cbtn")).to_have_count(2)


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
            const { Citation } = await import('/components/main/citation.js');
            window.__mount(Citation({ ordinal: 3, label: 'My doc' }));
        }"""
    )
    chip = page.locator(".cite")
    expect(chip).to_have_text("3")
    expect(chip).to_have_attribute("title", "My doc")
