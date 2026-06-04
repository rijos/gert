"""test_canvas.py — artifact tabs · rendered/source · sandboxed iframe · problems (§9).

Mounts artifacts directly via the harness where possible (no model round-trip
needed to prove the canvas viewers), and asserts the F3 sandbox attributes on the
HTML artifact iframe and the Problems panel on the code artifact.
"""

from __future__ import annotations

import pytest
from playwright.sync_api import Page, expect

# Deterministic harness-mount tests (no LLM/backend round-trip) — part of the CI
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
    # Flip to Source — the raw markup shows as text, never a live node.
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
