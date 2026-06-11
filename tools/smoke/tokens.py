"""tokens.py — the single source of dev JWTs for the Python E2E harness.

testing.md §4.3: Python mints the tokens; the Fake host only *trusts* a dev key
and validates through the SAME RS256/JWKS path it uses for Pocket ID in prod. So
the dev shortcut cannot hide a validation bug.

The RSA keypair is generated on first run under a git-ignored ``.dev/jwt/`` path
and reused thereafter; the matching ``dev-jwks.json`` is written beside it for the
host to trust. Nothing is committed — there is simply no key to leak.

The ``iss`` / ``aud`` stamped here MUST match the FakeE2E launch profile
(``Auth:Authority`` / ``Auth:Audience`` and ``Storage:ExpectedIssuer``). They are
the constants below; the launch profile points at the same values.

CLI::

    uv run python -m tools.smoke.tokens --role admin

prints a token plus a paste-ready ``window.GERT_DEV_TOKEN`` snippet (the launcher
injects the same global via an init script — see run.py).
"""

from __future__ import annotations

import argparse
import json
import time
from pathlib import Path
from typing import Any

import jwt
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import rsa

# --- the dev authority -------------------------------------------------------
# These two MUST agree with the FakeE2E launch profile (Auth:Authority /
# Auth:Audience) and with Storage:ExpectedIssuer. The folder key is
# sha256(iss + sub), and the provisioning gate (security F12) checks iss, so the
# issuer is load-bearing — not cosmetic.
ISSUER = "https://id.dev.local"
AUDIENCE = "gert-api"

# Standard JWT lifetime for a dev token (matches the host's ~1h prod window).
DEFAULT_LIFETIME_SECONDS = 3600

# --- role → distinguishing claims (mirrors .NET TestTokens) ------------------
# mint() adds iss/aud/exp/iat/nbf. Roles are data: a new privilege set is a
# one-line edit, an ad-hoc shape is a mint(role, **overrides) call.
ROLES: dict[str, dict[str, Any]] = {
    # admin surface + every tool.
    "admin": {"sub": "dev-admin", "groups": ["gert-admins"], "gert_tools": "*"},
    # standard non-admin; sandbox denied (rag + search + ask_user + fetch + memory).
    "user": {
        "sub": "dev-user",
        "groups": ["gert-users"],
        "gert_tools": "rag search ask_user fetch memory",
    },
    # restricted: only rag (search AND sandbox denied).
    "limited": {"sub": "dev-limited", "groups": ["gert-users"], "gert_tools": "rag"},
}

# --- key locations (git-ignored: .dev/ is in .gitignore) ---------------------
# Repo root is three levels up from this file: tools/smoke/tokens.py -> repo.
REPO_ROOT = Path(__file__).resolve().parents[2]
JWT_DIR = REPO_ROOT / ".dev" / "jwt"
PRIVATE_KEY_PATH = JWT_DIR / "dev-private.pem"
JWKS_PATH = JWT_DIR / "dev-jwks.json"

# A stable kid so the host can match the JWKS entry to the token header.
KEY_ID = "gert-dev-key-1"


def _b64url_uint(value: int) -> str:
    """Encode an unsigned int as a base64url string (no padding) for the JWK."""
    import base64

    raw = value.to_bytes((value.bit_length() + 7) // 8 or 1, "big")
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii")


def _write_jwks(private_key: rsa.RSAPrivateKey) -> None:
    """Write the public JWKS (RS256, use=sig) beside the private key."""
    public_numbers = private_key.public_key().public_numbers()
    jwk = {
        "kty": "RSA",
        "use": "sig",
        "alg": "RS256",
        "kid": KEY_ID,
        "n": _b64url_uint(public_numbers.n),
        "e": _b64url_uint(public_numbers.e),
    }
    JWKS_PATH.write_text(json.dumps({"keys": [jwk]}, indent=2), encoding="utf-8")


def ensure_keypair() -> rsa.RSAPrivateKey:
    """Load the dev RSA private key, generating (and writing the JWKS) on first run.

    Idempotent and safe to call from both ``tokens.py`` and the launcher: whichever
    runs first generates the pair; the other reuses it.
    """
    if PRIVATE_KEY_PATH.exists():
        private_key = serialization.load_pem_private_key(
            PRIVATE_KEY_PATH.read_bytes(), password=None
        )
        # Defensive: regenerate the JWKS if it went missing.
        if not JWKS_PATH.exists():
            _write_jwks(private_key)  # type: ignore[arg-type]
        return private_key  # type: ignore[return-value]

    JWT_DIR.mkdir(parents=True, exist_ok=True)
    private_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    PRIVATE_KEY_PATH.write_bytes(
        private_key.private_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PrivateFormat.PKCS8,
            encryption_algorithm=serialization.NoEncryption(),
        )
    )
    _write_jwks(private_key)
    return private_key


def mint(
    role: str, lifetime_seconds: int = DEFAULT_LIFETIME_SECONDS, **overrides: Any
) -> str:
    """Mint an RS256 JWT for ``role``, applying any claim ``overrides``.

    Stamps iss/aud/exp/iat/nbf to match the FakeE2E host. ``overrides`` tweak any
    claim without a new role — e.g. ``mint("user", gert_tools="rag search sandbox")``
    to prove the positive sandbox-entitlement path. An override of ``None`` OMITS
    that claim entirely (mirrors .NET ``TestTokens.Mint``): ``mint("user",
    gert_tools=None)`` mints a token with NO ``gert_tools`` claim — the fail-closed
    path, since the JWT is the sole grant source and there is no default grant
    (auth.md §10).
    """
    if role not in ROLES:
        raise ValueError(f"Unknown role {role!r}; use one of {sorted(ROLES)}.")

    private_key = ensure_keypair()
    now = int(time.time())

    claims = dict(ROLES[role])
    claims.update(overrides)
    # A None override removes the claim, so it is genuinely ABSENT (not null-valued).
    claims = {key: value for key, value in claims.items() if value is not None}

    payload = {
        **claims,
        "preferred_username": claims.get("sub", role),
        "iss": ISSUER,
        "aud": AUDIENCE,
        "iat": now,
        "nbf": now,
        "exp": now + lifetime_seconds,
    }

    pem = private_key.private_bytes(
        encoding=serialization.Encoding.PEM,
        format=serialization.PrivateFormat.PKCS8,
        encryption_algorithm=serialization.NoEncryption(),
    )
    return jwt.encode(
        payload,
        pem,
        algorithm="RS256",
        headers={"kid": KEY_ID},
    )


def _cli(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Mint a Gert dev JWT (RS256).")
    parser.add_argument(
        "--role",
        choices=sorted(ROLES),
        default="admin",
        help="The role whose claims to mint (default: admin).",
    )
    parser.add_argument(
        "--lifetime",
        type=int,
        default=DEFAULT_LIFETIME_SECONDS,
        help="Token lifetime in seconds (default: 3600).",
    )
    args = parser.parse_args(argv)

    token = mint(args.role, lifetime_seconds=args.lifetime)
    print(token)
    print()
    print("# Paste into the browser console to use the SPA with no Pocket ID setup:")
    # The SPA's services/auth.js consumes window.GERT_DEV_TOKEN in its dev branch.
    print(f'window.GERT_DEV_TOKEN = "{token}"; location.reload();')
    print()
    print(f"# JWKS the FakeE2E host trusts: {JWKS_PATH}")
    return 0


if __name__ == "__main__":
    raise SystemExit(_cli())
