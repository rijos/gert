"""test_rbac.py - RBAC + SSRF + IDOR + entitlement (section 9, security F5).

* **admin** sees ``/admin/users``; **user** does not (server returns 403).
* **SSRF (F5)**: a web search whose result set contains a private/link-local URL
  must be refused by the real adapter's fetch step - the metadata URL is never
  surfaced to the user. The adversarial result lives in ``fixtures.json``
  ("internal metadata") and is served by the SearXNG mock.
* **IDOR**: a user cannot open another user's document (a tampered project/doc id
  resolves only under the requester's own folder -> 404, never the other's data).
* **entitlement ceiling**: ``limited`` (only ``rag``) has Search + Sandbox dropped
  even if the UI/request asks for them.
"""

from __future__ import annotations

from playwright.sync_api import Page, expect

from tools.smoke import tokens
from tools.smoke.pages import AppPage


def test_admin_sees_user_list(admin_page: Page, base_url: str) -> None:
    admin_page.goto(f"{base_url}/admin/users")
    expect(admin_page.locator(".utable")).to_be_visible(timeout=10000)


def test_user_denied_admin(user_page: Page, base_url: str) -> None:
    user_page.goto(f"{base_url}/admin/users")
    # The table never renders for a non-admin (the API returns 403 to the list call).
    expect(user_page.locator(".utable")).to_have_count(0)


def test_admin_sees_button_and_can_open_panel(admin_page: Page, base_url: str) -> None:
    """The UI-discovery half of admin RBAC: an admin sees the shield button in the
    user chip, and clicking it lands on the admin panel (the user-list table
    renders). The server is the real gate (test_admin_sees_user_list); the button
    is only the discoverable entry point that gate backs."""
    app = AppPage(admin_page, base_url)
    app.goto(base_url, "/")
    app.wait_ready()
    expect(app.sidebar.admin_button).to_be_visible()
    app.sidebar.admin_button.click()
    expect(admin_page.locator(".utable")).to_be_visible(timeout=10000)


def test_user_has_no_admin_button(user_page: Page, base_url: str) -> None:
    """A non-admin never sees the admin shield - the entry point is hidden as well
    as gated (the API 403 is covered by test_user_denied_admin)."""
    app = AppPage(user_page, base_url)
    app.goto(base_url, "/")
    app.wait_ready()
    expect(app.sidebar.admin_button).to_have_count(0)


def test_ssrf_private_ip_result_refused(page: Page, base_url: str) -> None:
    """F5: the metadata URL from the adversarial fixture must never be fetched.

    Drives a search whose result set includes ``http://169.254.169.254/...``. The
    real SearXNG adapter's summarize/fetch step must refuse the link-local URL, so
    the metadata content never appears in the assistant's answer or any tool card.
    """
    app = AppPage(page)
    app.goto(base_url, "/")
    app.wait_ready()
    app.composer.send("search the web for internal metadata")
    app.thread.open_activity()
    expect(app.thread.tool_cards.first).to_be_visible(timeout=15000)
    # The forbidden metadata endpoint must NOT surface anywhere in the thread.
    expect(app.thread.root).not_to_contain_text("169.254.169.254")
    expect(app.thread.root).not_to_contain_text("meta-data")


def test_idor_cannot_open_other_users_document(user_page: Page, base_url: str) -> None:
    """A doc id from another user resolves only under the requester's folder -> 404."""
    # A well-formed-but-foreign doc id under the default project. The server keys the
    # folder off the token's sub, so this can only ever hit the requester's own data.
    # NOTE: context.request does NOT carry the SPA's in-memory bearer (security F2 -
    # the token never touches storage), so the probe authenticates explicitly: an
    # anonymous 401 would prove nothing about IDOR.
    foreign_doc = "00000000-0000-0000-0000-0000deadbeef"
    response = user_page.request.get(
        f"{base_url}/api/projects/default/documents/{foreign_doc}",
        headers={"Authorization": f"Bearer {tokens.mint('user')}"},
    )
    assert response.status in (404, 400), f"expected 404/400, got {response.status}"


def test_limited_role_drops_search_and_sandbox(
    limited_page: Page, base_url: str
) -> None:
    """The entitlement ceiling: limited (only rag) cannot invoke search/sandbox.

    Even when the request asks for the search tool, the server drops it (the claim
    is the ceiling), so no search tool card is produced.
    """
    app = AppPage(limited_page)
    app.goto(base_url, "/")
    app.wait_ready()
    # Ask for a web search; limited has no `search` entitlement -> the tool is dropped.
    app.composer.send("search the web for sqlite-vec benchmarks")
    # No web-search tool card should appear (the tag for search is "search").
    expect(app.thread.tool_card("Searching the web")).to_have_count(0)


def test_absent_gert_tools_grants_no_tools(untooled_page: Page, base_url: str) -> None:
    """The fail-closed floor: a JWT with NO ``gert_tools`` claim grants ZERO tools
    - not even ``rag``, which every standing role has. The JWT is the sole source
    of entitlement; there is no default grant (auth.md section 10).

    Driven through the canvas, which gives an unambiguous side-effect signal: the
    scripted model still emits a ``make_artifact`` call (the mock doesn't know the
    grant), but the orchestrator refuses it (the claim is the ceiling at execution
    time too), so NO artifact is persisted and NO canvas tab opens - even though
    the canned reply claims it did.
    """
    app = AppPage(untooled_page)
    app.goto(base_url, "/")
    app.wait_ready()
    app.composer.send("make me a demo html page")
    # The turn runs to completion (the model plays its scripted acknowledgement)...
    expect(app.thread.last_bot_body).to_contain_text("in the canvas.", timeout=15000)
    # ...but the make_artifact tool was refused, so nothing reached the canvas.
    expect(app.canvas.tab("html")).to_have_count(0)
