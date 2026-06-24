"""test_auth_smoke.py - API auth smoke: invalid/missing tokens are rejected.

Every ``/api/...`` endpoint sits behind the fallback authorization policy; only
``/healthz`` / ``/readyz`` are anonymous. This suite proves the negative space:

* **no token** -> 401 (with a ``WWW-Authenticate: Bearer`` challenge) on EVERY
  endpoint;
* **garbage token** -> 401 on EVERY endpoint;
* the full bad-token taxonomy (expired, wrong issuer, wrong audience, wrong
  signing key, ``alg=none``, empty/foreign scheme) -> 401 on one representative
  endpoint - the JWT validation pipeline is endpoint-agnostic, so one proves all.

Two positive sanity checks keep the suite honest: ``/healthz`` answers
anonymously, and a freshly minted dev token IS accepted - so the 401s above
cannot be the side-effect of an auth config that rejects everything.

These are plain HTTP assertions (httpx, no browser) against the running FakeE2E
host, which validates through the SAME RS256/JWKS path as prod (testing.md section 4.3):

    GERT_BASE_URL=http://127.0.0.1:5217 uv run pytest tools/smoke/tests/test_auth_smoke.py
"""

from __future__ import annotations

import time
from collections.abc import Iterator
from typing import Any

import httpx
import jwt
import pytest
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import rsa

# tokens lives in the parent package; tests run with the repo on sys.path.
from tools.smoke import tokens

# Deterministic, no LLM/backend round-trip - safe for the CI gate.
pytestmark = pytest.mark.component

# Placeholder route values: auth runs before model binding or any lookup, so
# these only need to be well-formed enough to match the route templates.
PID = "default"
GUID = "00000000-0000-0000-0000-000000000001"

# One (method, path) per controller action - keep in sync with
# src/Gert.Api/Controllers/*.cs when endpoints change. /healthz + /readyz are
# asserted anonymous below.
ENDPOINTS: list[tuple[str, str]] = [
    # ProjectsController
    ("GET", "/api/projects"),
    ("POST", "/api/projects"),
    ("GET", f"/api/projects/{PID}"),
    ("PATCH", f"/api/projects/{PID}"),
    ("DELETE", f"/api/projects/{PID}"),
    # ConversationsController
    ("GET", f"/api/projects/{PID}/conversations"),
    ("POST", f"/api/projects/{PID}/conversations"),
    ("GET", f"/api/projects/{PID}/conversations/{GUID}"),
    ("PATCH", f"/api/projects/{PID}/conversations/{GUID}"),
    ("DELETE", f"/api/projects/{PID}/conversations/{GUID}"),
    # ConversationEventsController
    ("GET", f"/api/projects/{PID}/conversations/{GUID}/events"),
    ("GET", f"/api/projects/{PID}/conversations/{GUID}/stream"),
    # MessagesController
    ("POST", f"/api/projects/{PID}/conversations/{GUID}/messages"),
    ("POST", f"/api/projects/{PID}/conversations/{GUID}/cancel"),
    # ArtifactsController
    ("GET", f"/api/projects/{PID}/conversations/{GUID}/artifacts"),
    ("GET", f"/api/projects/{PID}/artifacts/{GUID}"),
    # DocumentsController
    ("GET", f"/api/projects/{PID}/documents"),
    ("POST", f"/api/projects/{PID}/documents"),
    ("GET", f"/api/projects/{PID}/documents/{GUID}"),
    ("DELETE", f"/api/projects/{PID}/documents/{GUID}"),
    # AccountController
    ("POST", f"/api/projects/{PID}/forget-documents"),
    ("GET", f"/api/projects/{PID}/export"),
    ("GET", "/api/account/export"),
    ("DELETE", "/api/account"),
    # ModelsController / ToolsController / SettingsController
    ("GET", "/api/models"),
    ("GET", "/api/tools"),
    ("GET", "/api/settings"),
    ("PUT", "/api/settings"),
    # AdminController (admin policy on top of the fallback - still 401 anonymous)
    ("GET", "/api/admin/users"),
    ("GET", "/api/admin/users/dev-user"),
    ("DELETE", "/api/admin/users/dev-user"),
]

ENDPOINT_IDS = [f"{method} {path}" for method, path in ENDPOINTS]

# POST /documents binds IFormFile ([FromForm] inferred), so a body-less request is
# rejected by the consumes matcher DURING ROUTING (the branded /api 404 catch-all),
# never reaching auth. Send a real multipart part so the request exercises the
# auth middleware like a genuine upload would.
EXTRA_REQUEST_KWARGS: dict[tuple[str, str], dict[str, Any]] = {
    ("POST", f"/api/projects/{PID}/documents"): {
        "files": {"file": ("smoke.txt", b"auth smoke")}
    },
}

BAD_TOKEN_CASES = [
    "garbage",
    "empty-bearer",
    "basic-scheme",
    "expired",
    "wrong-issuer",
    "wrong-audience",
    "wrong-signing-key",
    "alg-none",
]


def _pem(private_key: rsa.RSAPrivateKey) -> bytes:
    return private_key.private_bytes(
        encoding=serialization.Encoding.PEM,
        format=serialization.PrivateFormat.PKCS8,
        encryption_algorithm=serialization.NoEncryption(),
    )


def _claims(**overrides: Any) -> dict[str, Any]:
    """The exact payload ``tokens.mint("user")`` stamps, with overrides allowed on
    the registered claims (mint() pins iss/aud/exp - here they ARE the test)."""
    now = int(time.time())
    return {
        **tokens.ROLES["user"],
        "preferred_username": "dev-user",
        "iss": tokens.ISSUER,
        "aud": tokens.AUDIENCE,
        "iat": now,
        "nbf": now,
        "exp": now + 3600,
        **overrides,
    }


def _forge(key: rsa.RSAPrivateKey | None = None, **overrides: Any) -> str:
    """An RS256 token signed with ``key`` (default: the trusted dev key) whose
    claims deviate from a valid token only by ``overrides``."""
    private_key = key if key is not None else tokens.ensure_keypair()
    return jwt.encode(
        _claims(**overrides),
        _pem(private_key),
        algorithm="RS256",
        headers={"kid": tokens.KEY_ID},
    )


@pytest.fixture(scope="session")
def api(base_url: str) -> Iterator[httpx.Client]:
    with httpx.Client(base_url=base_url, timeout=10.0) as client:
        yield client


@pytest.fixture(scope="session")
def bad_authorization() -> dict[str, str]:
    """Authorization header values that must ALL be rejected, keyed by case."""
    now = int(time.time())
    foreign_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    return {
        "garbage": "Bearer not-a-jwt",
        "empty-bearer": "Bearer",
        "basic-scheme": "Basic ZGV2OmRldg==",
        "expired": "Bearer " + _forge(iat=now - 7200, nbf=now - 7200, exp=now - 3600),
        "wrong-issuer": "Bearer " + _forge(iss="https://evil.example"),
        "wrong-audience": "Bearer " + _forge(aud="not-gert-api"),
        # Valid claims + the trusted kid, signed by a key the JWKS does not hold.
        "wrong-signing-key": "Bearer " + _forge(key=foreign_key),
        # Unsigned token; the host pins RS256, so alg=none must never validate.
        "alg-none": "Bearer " + jwt.encode(_claims(), "", algorithm="none"),
    }


@pytest.mark.parametrize(("method", "path"), ENDPOINTS, ids=ENDPOINT_IDS)
def test_no_token_is_rejected(api: httpx.Client, method: str, path: str) -> None:
    extra = EXTRA_REQUEST_KWARGS.get((method, path), {})
    response = api.request(method, path, **extra)
    assert response.status_code == 401, (
        f"{method} {path} without a token: expected 401, got {response.status_code}"
    )
    # The 401 must be the deliberate challenge (Program.cs OnChallenge writes a
    # branded ProblemDetails and suppresses the default empty body), not an
    # incidental error path.
    assert response.headers.get("content-type", "").startswith(
        "application/problem+json"
    ), f"{method} {path}: 401 was not the branded ProblemDetails challenge"


@pytest.mark.parametrize(("method", "path"), ENDPOINTS, ids=ENDPOINT_IDS)
def test_garbage_token_is_rejected(api: httpx.Client, method: str, path: str) -> None:
    extra = EXTRA_REQUEST_KWARGS.get((method, path), {})
    response = api.request(
        method, path, headers={"Authorization": "Bearer not-a-jwt"}, **extra
    )
    assert response.status_code == 401, (
        f"{method} {path} with a garbage token: expected 401, got {response.status_code}"
    )


@pytest.mark.parametrize("case", BAD_TOKEN_CASES)
def test_bad_token_taxonomy_is_rejected(
    api: httpx.Client, bad_authorization: dict[str, str], case: str
) -> None:
    response = api.get(
        "/api/projects", headers={"Authorization": bad_authorization[case]}
    )
    assert response.status_code == 401, (
        f"{case} token: expected 401, got {response.status_code}"
    )


def test_health_endpoints_are_anonymous(api: httpx.Client) -> None:
    assert api.get("/healthz").status_code == 200
    # /readyz is anonymous too; 503 just means a dependency reports degraded.
    assert api.get("/readyz").status_code in (200, 503)


def test_valid_token_is_accepted(api: httpx.Client) -> None:
    """Sanity: a freshly minted dev token passes - proves the 401s above come from
    token validation, not from an auth config that rejects everything."""
    token = tokens.mint("user")
    response = api.get("/api/projects", headers={"Authorization": f"Bearer {token}"})
    assert response.status_code == 200, (
        f"valid token rejected ({response.status_code}) - auth smoke is vacuous"
    )
