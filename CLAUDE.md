# CLAUDE.md

Gert — a privacy-first, self-hosted LLM chat server. ASP.NET Core 10 Web API (+ vendored
VanJS SPA) over **per-user SQLite**: there is no central database — every user is a folder
keyed by `sha256(iss + sub)` from the validated JWT, every project a subfolder with its own
`chat.db` + `rag.db`.

## Docs first

`docs/design/` is the source of truth for design and rationale. **Start at
[docs/design/README.md](docs/design/README.md)** — its "which doc for which change" table
routes you. When you change behaviour a design doc covers, update the doc in the same change;
code comments cite docs by section, so keep both ends accurate.

## Repo map

- `src/` — inward-only references (enforced by an architecture test):
  hosts (`Gert.Api`, `Gert.Console`) → adapters (`Gert.Authentication`, `Gert.Database.Sqlite`,
  `Gert.External`, `Gert.Storage`) → `Gert.Service` (all business logic, host-agnostic) →
  `Gert.Model` (POCOs, no deps).
- `src/Gert.Api/wwwroot/` — the SPA source: no npm, no build step, native ES modules.
- `tests/` — xUnit suites + shared fakes (`Gert.Testing`); real temp SQLite for repository tests.
- `tools/smoke/` — Python + Playwright E2E and mock upstreams (uv-managed).
- `docs/installation/configuration.md` — every operator knob.

## Commands

- `make build` / `make test` — .NET build (warnings are errors) / full test suite.
- `make lint` — ruff + mypy `--strict` over `tools/smoke`.
- `make check-links` — every relative link/anchor in tracked markdown resolves (CI gate;
  run after editing docs).
- `make smoke-auth` — boot Python mocks + the FakeE2E host, run the API auth smoke (no browsers).
- `make serve-mock [ROLE=admin|user|limited]` — run the real app against mock upstreams and
  open it in your own browser (mints a dev JWT; no Pocket ID needed).
- `make serve-mock-vllm VLLM_URL=http://host:8000/v1` — same, but chat hits a real vLLM.

## Rules that bite

- **The user key comes only from the validated token.** Never derive a user from a path,
  query, or body. The one request-supplied selector, `pid`, must only ever be joined *under*
  the token-derived folder ([docs/design/principles.md](docs/design/principles.md)).
- **Validation is fail-closed.** Every request DTO needs a registered `IValidator<T>`; a
  reflection meta-test goes red if one is missing.
- **No npm.** The SPA is no-build ESM with vendored libs; minification is .NET-only on publish.
- **No secrets in `appsettings.json`** — env vars / `dotnet user-secrets` only. Nothing under
  `.dev/` is ever committed.
- **Security findings F1–F12** ([docs/design/security.md](docs/design/security.md)) each have
  tests — don't weaken a control without reading its finding first.
