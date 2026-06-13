"""test_canvas.py - artifact tabs - rendered/source - sandboxed iframe - problems (section 9).

Mounts artifacts directly via the harness where possible (no model round-trip
needed to prove the canvas viewers), and asserts the F3 sandbox attributes on the
HTML artifact iframe and the Problems panel on the code artifact.
"""

from __future__ import annotations

import re

import pytest
from playwright.sync_api import Page, expect

# Deterministic harness-mount tests (no LLM/backend round-trip) - part of the CI
# gate via `run.py --pytest -m component`.
pytestmark = pytest.mark.component


def test_html_artifact_iframe_is_sandboxed(page: Page, base_url: str) -> None:
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { Artifact } = await import('/components/canvas/artifact.js');
            const node = Artifact({
                artifact: { id: 'a1', kind: 'html', name: 'demo.html',
                            content: '<h1>hi</h1>' },
                active: () => true,
            });
            window.__mount(node);
        }"""
    )
    frame = page.locator(".art-doc[data-type='html'] iframe")
    expect(frame).to_be_visible()
    # F3: scripts allowed for fidelity, but NEVER allow-same-origin (opaque origin).
    sandbox = frame.get_attribute("sandbox") or ""
    assert "allow-scripts" in sandbox
    assert "allow-same-origin" not in sandbox


def test_html_artifact_fallback_srcdoc_carries_restrictive_csp(
    page: Page, base_url: str
) -> None:
    """F3 FALLBACK half: when the served-origin ticket can't be obtained (here the
    artifact id isn't persisted -> 404), the viewer drops to an in-place srcdoc that
    still embeds a per-document CSP denying network/forms while keeping scripts for
    fidelity. (The PREFERRED served-origin path is covered by the e2e suite.)"""
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { Artifact } = await import('/components/canvas/artifact.js');
            const node = Artifact({
                artifact: { id: 'a1', kind: 'html', name: 'demo.html',
                            content: '<h1>hi</h1>' },
                active: () => true,
            });
            window.__mount(node);
        }"""
    )
    frame = page.locator(".art-doc[data-type='html'] iframe")
    # The ticket fetch 404s for this unpersisted id -> async fallback sets srcdoc.
    expect(frame).to_have_attribute("srcdoc", re.compile(r"Content-Security-Policy"))
    srcdoc = frame.get_attribute("srcdoc") or ""
    assert "default-src 'none'" in srcdoc
    assert "script-src 'unsafe-inline'" in srcdoc
    assert "form-action 'none'" in srcdoc
    # connect-src is never granted, so it falls back to default-src 'none'.
    assert "connect-src" not in srcdoc
    # The artifact body still renders.
    assert "<h1>hi</h1>" in srcdoc


def test_svg_artifact_csp_denies_scripts(page: Page, base_url: str) -> None:
    """The SVG viewer is script-free AND its per-document CSP says so."""
    page.goto(f"{base_url}/tests/harness.html")
    srcdoc = page.evaluate(
        """async () => {
            const { Artifact } = await import('/components/canvas/artifact.js');
            const node = Artifact({
                artifact: { id: 's1', kind: 'svg', name: 'art.svg',
                            content: '<svg xmlns=\\"http://www.w3.org/2000/svg\\"></svg>' },
                active: () => true,
            });
            window.__mount(node);
            return node.querySelector("iframe").srcdoc;
        }"""
    )
    assert "script-src 'none'" in srcdoc
    assert "default-src 'none'" in srcdoc


def test_html_artifact_source_view(page: Page, base_url: str) -> None:
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { Artifact } = await import('/components/canvas/artifact.js');
            window.__mount(Artifact({
                artifact: { id: 'a1', kind: 'html', name: 'demo.html', content: '<h1>hi</h1>' },
                active: () => true,
            }));
        }"""
    )
    # Flip to Source - the raw markup shows as text, never a live node.
    page.locator(".art-doc[data-type='html'] button", has_text="Source").click()
    page.wait_for_timeout(50)
    expect(page.locator(".art-doc[data-type='html'] .source-view")).to_contain_text(
        "<h1>hi</h1>"
    )


def test_code_artifact_problems_panel(page: Page, base_url: str) -> None:
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { Artifact } = await import('/components/canvas/artifact.js');
            window.__mount(Artifact({
                artifact: {
                    id: 'a2', kind: 'py', name: 'main.py',
                    content: 'import os\\nprint( 2+2 )',
                    problems: [{ severity: 'error', message: 'unused import: os',
                                 code: 'F401', line: 1, col: 8 }],
                },
                active: () => true,
            }));
        }"""
    )
    panel = page.locator(".art-doc[data-type='py'] .problems")
    expect(panel).to_be_visible()
    expect(panel.locator(".prob.err")).to_contain_text("unused import")


def test_artifact_tabs_switch(page: Page, base_url: str) -> None:
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { ArtifactTabs } = await import('/components/canvas/artifact-tabs.js');
            const artifacts = await import('/state/artifacts.js');
            artifacts.addArtifact({ id: 'm1', kind: 'md', name: 'readme.md', content: '# hi' });
            artifacts.addArtifact({ id: 'h1', kind: 'html', name: 'demo.html', content: '<b/>' });
            window.__mount(ArtifactTabs());
            await new Promise(r => setTimeout(r));
        }"""
    )
    tabs = page.locator(".ctabs .ctab")
    expect(tabs).to_have_count(2)
    expect(page.locator(".ctab[data-tab='md']")).to_be_visible()
    expect(page.locator(".ctab[data-tab='html']")).to_be_visible()


def test_jump_to_present_pill_stays_opaque_on_hover(page: Page, base_url: str) -> None:
    """The pill floats over message text, so its hover background must stay
    fully opaque - a translucent hover tint lets the text bleed through."""
    page.goto(f"{base_url}/tests/harness.html")
    page.evaluate(
        """async () => {
            const { MessageStream } = await import('/components/main/message-stream.js');
            const chat = await import('/state/chat.js');
            chat.messages.length = 0;
            for (let i = 0; i < 40; i++) {
                chat.messages.push(chat.reactiveMessage({
                    id: 'm' + i, role: i % 2 ? 'assistant' : 'user',
                    text: 'line ' + i, streaming: false,
                }));
            }
            const node = MessageStream();
            node.style.height = '300px';
            window.__mount(node);
        }"""
    )
    stream = page.locator(".stream")
    # Scroll well clear of the bottom -> the pill appears.
    stream.evaluate(
        "el => { el.scrollTop = 0; el.dispatchEvent(new Event('scroll')); }"
    )
    pill = page.locator(".jump-present")
    expect(pill).to_be_visible()

    pill.hover()
    page.wait_for_timeout(200)  # let the --t-fast transition settle
    bg = pill.evaluate("el => getComputedStyle(el).backgroundColor")
    # rgba(...) with alpha < 1 (or 'transparent') means see-through: a bug.
    assert "rgba" not in bg.replace(" ", "") or bg.endswith(", 1)"), (
        f"jump-to-present hover background is not opaque: {bg}"
    )

    # ...and clicking it returns the reader to the present (pill disappears).
    pill.click()
    expect(pill).not_to_be_visible()
