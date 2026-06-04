"""conftest.py — pytest + Playwright fixtures for the Gert E2E tests.

These tests assume a running FakeE2E host + mocks (boot them with
``uv run python -m tools.smoke.run --base-url ...`` or via the CI web job, then
``GERT_BASE_URL=... uv run pytest tools/smoke/tests``). They are **browser tests**
and need ``uv run playwright install`` — only CI/staging has the browsers.

The one exception is ``test_embeddings_conformance.py``, which imports no browser
fixture and runs anywhere ``uv`` can.

Token injection mirrors the launcher: a per-role init script seeds
``window.GERT_DEV_TOKEN``, which the SPA's dev-only ``ensureSession`` branch reads
(the token is otherwise in-memory only — security F2).
"""

from __future__ import annotations

import os
from collections.abc import Iterator

import pytest
from playwright.sync_api import Browser, BrowserContext, Page

# tokens lives in the parent package; tests run with the repo on sys.path.
from tools.smoke import tokens


def pytest_addoption(parser: pytest.Parser) -> None:
    parser.addoption(
        "--gert-base-url",
        action="store",
        default=os.environ.get("GERT_BASE_URL", "http://127.0.0.1:5217"),
        help="Base URL of the running FakeE2E host.",
    )


@pytest.fixture(scope="session")
def base_url(request: pytest.FixtureRequest) -> str:
    value = request.config.getoption("--gert-base-url")
    return str(value).rstrip("/")


def _make_context(browser: Browser, role: str) -> BrowserContext:
    token = tokens.mint(role)
    context = browser.new_context()
    context.add_init_script(f"window.GERT_DEV_TOKEN = {token!r};")
    return context


@pytest.fixture
def admin_page(browser: Browser, base_url: str) -> Iterator[Page]:
    context = _make_context(browser, "admin")
    page = context.new_page()
    yield page
    context.close()


@pytest.fixture
def user_page(browser: Browser, base_url: str) -> Iterator[Page]:
    context = _make_context(browser, "user")
    page = context.new_page()
    yield page
    context.close()


@pytest.fixture
def limited_page(browser: Browser, base_url: str) -> Iterator[Page]:
    context = _make_context(browser, "limited")
    page = context.new_page()
    yield page
    context.close()


@pytest.fixture
def page(admin_page: Page) -> Page:
    """Default page: an admin context (the common case for most scenarios)."""
    return admin_page
