# Gert E2E smoke harness

Python + Playwright that clicks through the real SPA as admin / user / limited
across Chromium and Firefox, plus the **mock upstreams** (vLLM + SearXNG) the real
`Gert.External` adapters point at in the `FakeE2E` host profile. uv-managed — no
npm, no Node. See [`docs/design/testing.md`](../../docs/design/testing.md) §3, §4,
§8, §9, §11 and Appendix A.

## Setup

```bash
cd tools/smoke
uv venv
uv pip install -r requirements.txt
uv run playwright install chromium firefox   # CI/staging only — installs browsers
```

## Run

```bash
# From the repo root (so `tools.smoke.*` imports resolve):

# Full matrix: boots mocks + the FakeE2E host, mints tokens, runs scenarios.
uv run python -m tools.smoke.run

# Attach to an already-running host+mocks (e.g. CI booted them in the background):
uv run python -m tools.smoke.run --base-url http://127.0.0.1:5217

# Narrow it down:
uv run python -m tools.smoke.run --browser chromium --role admin --headed --keep-open

# Pytest-driven scenarios (host must already be up):
GERT_BASE_URL=http://127.0.0.1:5217 uv run pytest tools/smoke/tests
```

## Mint a local dev token (no Pocket ID setup)

```bash
uv run python -m tools.smoke.tokens --role admin
```

Prints an RS256 JWT plus a paste-ready `window.GERT_DEV_TOKEN = "…"; location.reload();`
snippet. The keypair is generated on first run under the **git-ignored**
`.dev/jwt/` and reused thereafter; `dev-jwks.json` is written beside it for the
host to trust. The `iss`/`aud` match the `FakeE2E` launch profile.

## What runs without a browser

These need only `uv` (no `playwright install`):

```bash
# A.2 / A.5 embeddings conformance (cross-language anti-drift vs the .NET fake):
uv run pytest tools/smoke/tests/test_embeddings_conformance.py

# Boot the mock upstreams standalone:
uv run python -m tools.smoke.mocks

# Mint a token (exercises the RS256 keygen + JWKS):
uv run python -m tools.smoke.tokens --role user
```

The browser scenarios (`test_chat`, `test_knowledge`, `test_canvas`, `test_rbac`,
`test_chrome`, `test_components`) require installed browsers and are **deferred to
CI/staging** — they are written to run, but this repo's sandbox does not install
browsers.

## Token injection (why not localStorage)

`services/auth.js` keeps the access token in an **in-memory** module variable
(security F2) — never localStorage. So the launcher/tests seed
`window.GERT_DEV_TOKEN` via a Playwright **init script** (runs before any app
module), and a dev-only branch in `ensureSession` installs it as the in-memory
bearer. Production never sets that global, so the branch is inert there.

## WebSocket and the dev proxy

The chat SPA prefers WS for live turn delivery, but `proxy.py` (the
`make serve-mock` viewing path) is plain HTTP and does **not** upgrade
WebSockets — through the proxy the SPA's transport chain falls back to the SSE
stream endpoint automatically (then to range polling), so streaming still
works. WS is exercised when the browser talks to the FakeE2E host directly
(the Playwright matrix and `--serve`).

## Artifacts

Traces + screenshots on failure land under `tools/smoke/artifacts/`
(git-ignored).
