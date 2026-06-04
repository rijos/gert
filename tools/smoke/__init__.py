"""Gert E2E smoke harness (testing.md §9).

Mock upstreams (``mocks/``), RS256 dev-token minting (``tokens.py``), SPA page
objects (``pages.py``), the launcher (``run.py``), and Playwright/pytest scenarios
(``tests/``). uv-managed; no npm, no Node. Browsers are installed only in
CI/staging.
"""
