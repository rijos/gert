"""test_style.py - visual/style invariants over harness-mounted components.

Pin the UI contracts that pure DOM assertions miss: every artifact kind shares
ONE header height; the render/raw kinds carry a Preview/Source toggle while a
code artifact shows only its highlighted preview; the ask_user question card
renders its option list; and the design tokens actually resolve (a broken
tokens.css would zero them out silently). All deterministic harness mounts - no
model round-trip.
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
    """Every artifact type renders the same chrome at the same height. Code kinds drop the
    Preview/Source toggle, so the head is height-anchored (artifact.js) to keep tabs from
    jumping when switching between a code tab and a render/raw tab."""
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


def test_only_non_code_kinds_have_a_preview_source_toggle(
    page: Page, base_url: str
) -> None:
    """The render/raw kinds (md/html/svg) carry the Preview/Source toggle; a code artifact's
    preview IS its highlighted source, so it has none."""
    _mount_all_kinds(page, base_url)
    for kind in ("md", "html", "svg"):
        seg = page.locator(f".art-doc[data-type='{kind}'] .art-head .seg .sgb")
        expect(seg).to_have_count(2)
    # the code kind shows only the preview - no toggle.
    expect(page.locator(".art-doc[data-type='py'] .art-head .seg")).to_have_count(0)


def test_code_artifact_shows_only_the_numbered_preview(
    page: Page, base_url: str
) -> None:
    """Code artifacts render just the numbered, highlighted preview - no toggle, and the raw
    source pane is never shown."""
    _mount_all_kinds(page, base_url)
    doc = page.locator(".art-doc[data-type='py']")
    # the numbered, linted preview is visible and carries the code...
    expect(doc.locator(".code-scroll .cline .lnum").first).to_be_visible()
    expect(doc.locator(".code-scroll")).to_contain_text("print(2 + 2)")
    # ...with no toggle and no raw source pane shown.
    expect(doc.locator(".art-head .seg")).to_have_count(0)
    expect(doc.locator(".source-view")).not_to_be_visible()


def test_ask_user_question_card_renders_options_and_answered_state(
    page: Page, base_url: str
) -> None:
    """The single-question card: question text + one button per option + the
    free-text input while pending; flipping to answered echoes the answer and
    retires the inputs (the wire driving that flip is the question_answered
    event - covered by the LLM-tools E2E; this pins the visual contract)."""
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { QuestionCard } = await import('/components/main/question-card.js');
            const { reactive } = await import('/lib/van-x.js');
            window.__q = reactive({
                questionId: 'q-harness-1',
                items: [{
                    text: 'Which database do you prefer?',
                    header: '',
                    options: ['sqlite-vec', 'qdrant', 'pgvector'],
                    allowFreeText: true,
                    value: '',
                }],
                answered: false, answers: [], expired: false, posting: false,
            });
            window.__mount(QuestionCard(window.__q));
        }"""
    )
    card = page.locator(".qcard")
    expect(card).to_be_visible()
    # A single question shows no tab strip - just the one prompt.
    expect(card.locator(".qtab")).to_have_count(0)
    expect(card.locator(".qtext")).to_contain_text("Which database")
    expect(card.locator(".qopt")).to_have_count(3)
    expect(card.locator(".qinput")).to_be_visible()
    expect(card.locator(".qsend")).to_have_text("Answer")

    # The answered state: the answer echoed, inputs retired.
    page.evaluate(
        "() => { window.__q.answered = true; window.__q.answers = ['sqlite-vec']; }"
    )
    expect(card.locator(".qanswer")).to_contain_text("sqlite-vec")
    expect(card.locator(".qinput")).not_to_be_visible()
    expect(card.locator(".qsend")).to_have_count(0)


def test_ask_user_question_card_renders_tabs_for_multiple_questions(
    page: Page, base_url: str
) -> None:
    """The multi-question card: one tab per question (its header, or
    "Question N"), the active tab's prompt + inputs, and one Submit for all.
    Switching tabs shows the other question; answering both echoes each."""
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { QuestionCard } = await import('/components/main/question-card.js');
            const { reactive } = await import('/lib/van-x.js');
            window.__q = reactive({
                questionId: 'q-harness-2',
                items: [
                    { text: 'Which database?', header: 'DB',
                      options: ['sqlite-vec', 'qdrant'], allowFreeText: false, value: '' },
                    { text: 'Anything else?', header: 'Notes',
                      options: [], allowFreeText: true, value: '' },
                ],
                answered: false, answers: [], expired: false, posting: false,
            });
            window.__mount(QuestionCard(window.__q));
        }"""
    )
    card = page.locator(".qcard")
    expect(card.locator(".qtab")).to_have_count(2)
    expect(card.locator(".qtab.active")).to_have_text("DB*")
    expect(card.locator(".qtext")).to_contain_text("Which database")
    # The first tab is closed (no free-text input); submit blocked until both answered.
    expect(card.locator(".qinput")).to_have_count(0)
    expect(card.locator(".qsend")).to_be_disabled()

    # The second tab shows the open question's text input.
    card.locator(".qtab").nth(1).click()
    expect(card.locator(".qtext")).to_contain_text("Anything else")
    expect(card.locator(".qinput")).to_be_visible()

    # Answered: each question's answer is echoed with its tab label.
    page.evaluate(
        "() => { window.__q.answered = true; window.__q.answers = ['qdrant', 'looks good']; }"
    )
    expect(card.locator(".qanswer")).to_have_count(2)
    expect(card.locator(".qanswer").first).to_contain_text("qdrant")
    expect(card.locator(".qanswer").nth(1)).to_contain_text("looks good")
    expect(card.locator(".qtab")).to_have_count(0)


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


def test_minify_css_strips_comments_and_collapses_whitespace(
    page: Page, base_url: str
) -> None:
    """component.js minifyCss() (and the matching build-time pass in Gert.Web.Bundle) strips
    comments + whitespace from component stylesheets while preserving selector structure and
    content values - so shipped CSS carries no banners/indentation."""
    page.goto(f"{base_url}/tests/harness.html")
    out = page.evaluate(
        r"""async () => {
            const { minifyCss } = await import('/lib/component.js');
            return minifyCss(`
                /* a comment */
                .a .b {
                  color: red;
                  content: "";
                }
            `);
        }"""
    )
    # comment + newlines gone; descendant combinator and content:"" survive intact.
    assert out == '.a .b{color: red;content: ""}', out


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
