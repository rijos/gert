"""test_style.py - visual/style invariants over harness-mounted components.

Pin the UI contracts that pure DOM assertions miss: every artifact kind shares
ONE header height and the Preview/Source toggle, the code viewer grew a real
source mode, the ask_user question card renders its option list, and the
design tokens actually resolve (a broken tokens.css would zero them out
silently). All deterministic harness mounts - no model round-trip.
"""

from __future__ import annotations

import pytest
from playwright.sync_api import Page, expect

pytestmark = pytest.mark.component

_KINDS = [
    ("md", "notes.md", "# hi"),
    ("html", "demo.html", "<h1>hi</h1>"),
    ("svg", "art.svg", '<svg xmlns="http://www.w3.org/2000/svg"></svg>'),
    ("py", "main.py", "print(2 + 2)"),
]


def _mount_all_kinds(page: Page, base_url: str) -> None:
    # ONE __mount call: the harness root is wiped per mount, so the four
    # viewers ride a single wrapper.
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async (kinds) => {
            const { Artifact } = await import('/components/canvas/artifact.js');
            const wrap = document.createElement('div');
            for (const [kind, name, content] of kinds) {
                wrap.append(Artifact({
                    artifact: { id: 'a-' + kind, kind, name, content },
                    active: () => true,
                }));
            }
            window.__mount(wrap);
        }""",
        [[k, n, c] for k, n, c in _KINDS],
    )


def test_artifact_header_height_is_uniform_across_kinds(
    page: Page, base_url: str
) -> None:
    """Every artifact type renders the same chrome at the same height - the
    code kinds used to swap the seg toggle for a shorter problems line, so the
    header jumped when switching tabs."""
    _mount_all_kinds(page, base_url)
    heights: dict[str, float] = {}
    for kind, _, _ in _KINDS:
        head = page.locator(f".art-doc[data-type='{kind}'] .art-head")
        expect(head).to_be_visible()
        box = head.bounding_box()
        assert box is not None, f"no header box for {kind}"
        heights[kind] = box["height"]
    distinct = set(heights.values())
    assert len(distinct) == 1, f"artifact header heights differ by kind: {heights}"


def test_every_artifact_kind_has_a_preview_source_toggle(
    page: Page, base_url: str
) -> None:
    _mount_all_kinds(page, base_url)
    for kind, _, _ in _KINDS:
        seg = page.locator(f".art-doc[data-type='{kind}'] .art-head .seg .sgb")
        expect(seg).to_have_count(2)


def test_code_artifact_source_mode_shows_raw_unnumbered_text(
    page: Page, base_url: str
) -> None:
    _mount_all_kinds(page, base_url)
    doc = page.locator(".art-doc[data-type='py']")
    # Preview: the numbered, linted view.
    expect(doc.locator(".code-scroll .cline .lnum").first).to_be_visible()
    # Source: the raw text without the gutter, clean to copy.
    doc.locator(".art-head button", has_text="Source").click()
    expect(doc.locator(".source-view")).to_be_visible()
    expect(doc.locator(".source-view")).to_contain_text("print(2 + 2)")
    expect(doc.locator(".code-scroll")).not_to_be_visible()


def test_ask_user_question_card_renders_options_and_answered_state(
    page: Page, base_url: str
) -> None:
    """The ask list: question text + one button per option + the free-text
    input while pending; flipping to answered highlights the chosen option and
    retires the inputs (the wire driving that flip is the question_answered
    event - covered by the LLM-tools E2E; this pins the visual contract)."""
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { QuestionCard } = await import('/components/main/question-card.js');
            const { reactive } = await import('van-x');
            window.__q = reactive({
                questionId: 'q-harness-1',
                text: 'Which database do you prefer?',
                options: ['sqlite-vec', 'qdrant', 'pgvector'],
                allowFreeText: true,
                answered: false, answer: null, expired: false, posting: false,
            });
            window.__mount(QuestionCard(window.__q));
        }"""
    )
    card = page.locator(".qcard")
    expect(card).to_be_visible()
    expect(card.locator(".qtext")).to_contain_text("Which database")
    expect(card.locator(".qopt")).to_have_count(3)
    expect(card.locator(".qinput")).to_be_visible()

    # The answered state: chosen option highlighted, inputs retired.
    page.evaluate(
        "() => { window.__q.answered = true; window.__q.answer = 'sqlite-vec'; }"
    )
    expect(card.locator(".qopt.chosen")).to_have_text("sqlite-vec")
    expect(card.locator(".qinput")).not_to_be_visible()
    expect(card.locator(".qopt").first).to_be_disabled()


def test_design_tokens_resolve_to_real_values(page: Page, base_url: str) -> None:
    """The token sheet is the single styling source (spa-style-guide): if the
    custom properties stop resolving, every component silently falls back to
    UA defaults - pin a few load-bearing ones."""
    page.goto(f"{base_url}/tests/harness.html")
    tokens = page.evaluate(
        """() => {
            const s = getComputedStyle(document.documentElement);
            return {
                coral: s.getPropertyValue('--coral').trim(),
                bg: s.getPropertyValue('--bg').trim(),
                line: s.getPropertyValue('--line').trim(),
                sans: s.getPropertyValue('--sans').trim(),
                mono: s.getPropertyValue('--mono').trim(),
                headH: s.getPropertyValue('--head-h').trim(),
            };
        }"""
    )
    for name, value in tokens.items():
        assert value, f"design token --{name} resolved to nothing"
    assert tokens["headH"].endswith("px")


def test_theme_attribute_flips_token_values(page: Page, base_url: str) -> None:
    """Manila/Ember both come from tokens.css light-dark(): forcing the theme
    attribute must actually change the resolved paper color."""
    page.goto(f"{base_url}/tests/harness.html")
    backgrounds = page.evaluate(
        """() => {
            const read = () => getComputedStyle(document.body).backgroundColor;
            document.documentElement.setAttribute('data-theme', 'manila');
            const light = read();
            document.documentElement.setAttribute('data-theme', 'ember');
            const dark = read();
            document.documentElement.removeAttribute('data-theme');
            return { light, dark };
        }"""
    )
    assert backgrounds["light"] != backgrounds["dark"], (
        f"theme flip changed nothing: {backgrounds}"
    )
