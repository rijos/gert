"""conftest.py - pytest + Playwright fixtures for the Gert E2E tests.

These tests assume a running FakeE2E host + mocks (boot them with
``uv run python -m tools.smoke.run --base-url ...`` or via the CI web job, then
``GERT_BASE_URL=... uv run pytest tools/smoke/tests``). They are **browser tests**
and need ``uv run playwright install`` - only CI/staging has the browsers.

The one exception is ``test_embeddings_conformance.py``, which imports no browser
fixture and runs anywhere ``uv`` can.

Token injection mirrors the launcher: a per-role init script seeds
``window.GERT_DEV_TOKEN``, which the SPA's dev-only ``ensureSession`` branch reads
(the token is otherwise in-memory only - security F2).
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
    return _context_for_token(browser, tokens.mint(role))


def _context_for_token(browser: Browser, token: str) -> BrowserContext:
    # Pin the timezone so clock-dependent assertions are deterministic
    # regardless of the host machine's locale.
    context = browser.new_context(timezone_id="UTC")
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
def untooled_page(browser: Browser, base_url: str) -> Iterator[Page]:
    """A user whose JWT carries NO ``gert_tools`` claim - the fail-closed path.

    The JWT is the sole source of tool entitlement: there is no default grant, so
    an absent claim yields ZERO tools (auth.md section 10). ``mint(..., gert_tools=None)``
    omits the claim entirely (not a null value), exercising exactly that path.
    """
    context = _context_for_token(browser, tokens.mint("user", gert_tools=None))
    page = context.new_page()
    yield page
    context.close()


@pytest.fixture
def page(admin_page: Page) -> Page:
    """Default page: an admin context (the common case for most scenarios)."""
    return admin_page


@pytest.fixture
def clean_documents(page: Page) -> Iterator[Page]:
    """Delete any knowledge-base docs a test ingests, restoring the empty project.

    The host boots one shared per-user project and only wipes it at startup, so a
    doc a test uploads otherwise persists for every later test (across browsers).
    Once the project holds docs the SPA prepends a "Documents in this project: ..."
    line to each turn, which breaks the prompt-exact echo assertions elsewhere.
    Teardown clears the docs via the documents service so each doc test is hermetic.
    """
    yield page
    page.evaluate(
        """async () => {
            const svc = await import('/services/documents.js');
            const docs = await svc.list();
            for (const d of docs || []) await svc.remove(d.id);
        }"""
    )
