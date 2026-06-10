# Configuring Gert

The operator's reference: every server-level knob, where it binds from, and a working
example for each. This is the **installation** view — the *design* of the configuration
cascade (server → user → project → conversation) and the project model live in
[design/configuration.md](../design/configuration.md).

---

## 1. How configuration binds

Standard ASP.NET Core layering, last one wins:

```
appsettings.json  →  appsettings.{Environment}.json  →  environment variables  →  command line
```

- **appsettings.json** (`src/Gert.Api/appsettings.json`) — non-secret defaults. This is
  where a deployment's static config lives.
- **Environment variables** — replace `:` with `__`:
  `Gert__Vllm__BaseUrl=http://vllm:8000`. Array entries by index:
  `Gert__Models__0__Id=qwen36`.
- **Command line** — `dotnet run --project src/Gert.Api -- --Gert:Vllm:BaseUrl=http://vllm:8000`.
  Highest precedence; beats launch-profile environment variables too.

**Secrets never go in appsettings.json** (security F8). The vLLM bearer key, if your
upstream needs one, arrives via an environment variable or `dotnet user-secrets`:

```bash
dotnet user-secrets --project src/Gert.Api set "Gert:Vllm:ApiKey" "sk-…"
# or
export Gert__Vllm__ApiKey=sk-…
```

---

## 2. Minimal working example

A single vLLM box serving one chat model plus the embedding model:

```jsonc
{
  "Gert": {
    "Vllm": {
      "BaseUrl": "http://192.168.10.99:8000",   // NO trailing /v1 — Gert appends /v1/… itself
      "ChatModelId": "qwen36",
      "EmbeddingModelId": "bge-m3",
      "EmbeddingDimensions": 1024
    },
    "Models": [
      {
        "Id": "qwen36",
        "Name": "Qwen 3.6 27B",
        "Default": true,
        "Capabilities": [ "tools", "vision" ],
        "Context": 131072,
        "Endpoint": ":8000"
      }
    ],
    "Search": { "BaseUrl": "http://localhost:8080" }
  },
  "Auth": {
    "Authority": "https://id.example.com",
    "Audience": "gert-api"
  },
  "Storage": {
    "DataRoot": "/data",
    "ExpectedIssuer": "https://id.example.com"
  }
}
```

> **The `/v1` gotcha:** `Gert:Vllm:BaseUrl` is the *server* base
> (`http://host:8000`), not the OpenAI API base (`http://host:8000/v1`). The adapters
> request absolute `/v1/chat/completions` and `/v1/embeddings` paths against it, so a
> pasted `/v1` suffix would double up.

---

## 3. `Gert:Vllm` — the model upstream

One OpenAI-compatible upstream serves **both chat and embeddings**. Binds to
`VllmOptions` (`src/Gert.External/Vllm/VllmOptions.cs`).

| Key | Default | Notes |
|-----|---------|-------|
| `BaseUrl` | `http://localhost:8000` | Server base URL, **without** `/v1`. |
| `ApiKey` | *(unset)* | **Secret** — env / user-secrets only. Sent as `Authorization: Bearer`. |
| `ChatModelId` | `default` | Sent as `model` on `/v1/chat/completions`. Must be a model id your server actually serves (check `GET <BaseUrl>/v1/models`). |
| `EmbeddingModelId` | `bge-m3` | Sent as `model` on `/v1/embeddings`. Knowledge upload (RAG) fails if the upstream doesn't serve it. |
| `EmbeddingDimensions` | `1024` | Must match the embedding model (bge-m3 = 1024). Effectively immutable once data exists — it bakes into every `rag.db` ([design §4](../design/configuration.md)). |
| `RequestTimeoutSeconds` | `120` | Max wait, **per attempt**, for vLLM to *accept* a request (time to response headers) — **not** the stream duration; the chat stream is bounded by the turn budget ([§9](#9-gertturn--the-detached-turn-pipeline)). |
| `RetryCount` | `2` | Retries on transient pre-stream (connect/headers) failures, for chat and embeddings. Safe for chat — a retried attempt means no tokens were streamed. `0` disables. |

> **vLLM prefix caching.** Gert's requests are built to be prefix-cache friendly:
> the system prompt is static per project, history is replayed verbatim, tool specs
> serialize deterministically, and no per-request unique fields are sent. To benefit,
> make sure the vLLM server has automatic prefix caching enabled — it is **on by
> default in the V1 engine**; verify with `--enable-prefix-caching` (and watch the
> `vllm:prefix_cache_hit_rate` metrics). Two caveats: prefix reuse is shared across
> all users of the server (fine for a single-tenant box; pass a per-tenant
> `cache_salt` upstream if you ever need isolation), and editing a project's pinned
> instructions invalidates every conversation prefix in that project.

---

## 4. `Gert:Models` — the model catalog

What `GET /api/models` publishes to the picker, **in configured order**. Binds to
`ModelInfo` entries (`src/Gert.Model/ModelInfo.cs`). When the section is absent or
empty, the catalog falls back to a single entry built from `Gert:Vllm:ChatModelId`, so
the picker always has one real option.

| Key | Required | Notes |
|-----|----------|-------|
| `Id` | yes | Sent upstream as `model`. Must exist on the vLLM (`GET /v1/models`). |
| `Name` | yes | Display name in the picker. |
| `Default` | no | The server-level default of the config cascade. Flag exactly one. |
| `Capabilities` | no | Capability tokens, shown as badges. `"tools"` is **load-bearing**: it gates tool calling. **Unset (null) means permissive** — the model is assumed tool-capable; an explicit list *without* `"tools"` (e.g. `["text only"]`) disables tools for that model. Other tokens (`"vision"`, …) are display-only today. |
| `Context` | no | Context window in tokens — the "128K ctx" badge. vLLM reports it as `max_model_len` on `GET /v1/models`. Unset hides the badge. |
| `Fast` | no | Display-only "· fast" marker. |
| `Endpoint` | no | Display-only endpoint hint, e.g. `:8000`. |

Filling in `Context` and `Capabilities` is on you, the operator — Gert binds this
statically at startup and never probes the upstream. Two quick ways to find the values:

```bash
# context window:
curl -s http://192.168.10.99:8000/v1/models | jq '.data[] | {id, max_model_len}'

# tools / vision: the dev harness probes them and prints a line per model at boot —
make serve-mock-vllm VLLM_URL=http://192.168.10.99:8000/v1
#   qwen36: 131072 ctx, tools/vision
```

Users pick from this catalog; they can never add endpoints or model ids of their own
([design §4](../design/configuration.md#4-llm-providers--models)).

---

## 5. `Auth` + `Storage` — identity and the data root

| Key | Notes |
|-----|-------|
| `Auth:Authority` | The OIDC issuer (Pocket ID). JWTs are validated against its JWKS, RS256 only. |
| `Auth:Audience` | Expected `aud` claim, e.g. `gert-api`. |
| `Storage:DataRoot` | Filesystem root holding the `users/` tree. Everything Gert stores lives here; back up this directory and you've backed up Gert. |
| `Storage:ExpectedIssuer` | Fail-closed `iss` assertion checked **before** any user folder is created (F12). Normally equal to `Auth:Authority`. |

Both `Storage` values are **required** — the host refuses to start without them.

Dev-only escape hatch: `Gert:Dev:JwksPath` points at a local JWKS file so the test
harness can mint tokens offline. It is rejected in the Production environment; never
set it on a real deployment.

---

## 6. `Gert:Search` — SearXNG web search

Binds to `SearXngOptions`. The fetch step (downloading result pages) is the
SSRF-exposed part and is **off by default**.

| Key | Default | Notes |
|-----|---------|-------|
| `BaseUrl` | `http://localhost:8080` | The SearXNG instance. |
| `FetchPages` | `false` | Fetch + summarize result pages (SSRF-guarded). |
| `MaxFetch` | `3` | Pages fetched per search when enabled. |
| `MaxFetchBytes` | `2097152` | Body-size cap per fetched page. |
| `FetchTimeoutSeconds` | `10` | Wall-clock cap per page fetch. |
| `MaxRedirects` | `3` | Each hop re-vetted by the SSRF guard. |
| `SearchTimeoutSeconds` | `15` | Total budget for the search API call, retries included; the HTTP client timeout sits 1 s above as a backstop. |

---

## 7. `Gert:Sandbox` — the `run_python` sandbox

Binds to `SandboxOptions`. Two backends sit behind one `ISandbox` port; `Backend`
picks. The defaults *are* the security posture: egress off, no `/data` mount, hard
caps. Raise them knowingly.

| Key | Default | Notes |
|-----|---------|-------|
| `Backend` | `monty` | Which backend runs `run_python`: `monty` (Pydantic's Rust Python interpreter via the sidecar — no container infra) or `gvisor` (runsc container). An unknown value fails fast at startup. |
| `WallClockSeconds` | `10` | Kill timeout per run (both backends). |
| `MemoryMiB` | `256` | Memory limit (both backends). |
| `MaxOutputBytes` | `65536` | Captured stdout/stderr cap (both backends). |
| `CpuSeconds` | `5` | CPU-time limit (gVisor only). |
| `PidLimit` | `64` | Max processes/threads (gVisor only). |
| `TmpSizeMiB` | `32` | Writable `/tmp`; rootfs stays read-only (gVisor only). |
| `RunscPath` | `runsc` | Path to the gVisor binary (gVisor only). |
| `Image` | `gert-sandbox-python` | OCI bundle with a Python runtime (gVisor only). |
| `EgressEnabled` | `false` | gVisor outbound network — the exfiltration brake. Leave off unless you must. (Monty has no network at all.) |

### 7a. `Gert:Sandbox:Monty` — the monty sidecar (`Backend=monty`)

Binds to `MontyOptions`. Run the sidecar from [tools/monty](../../tools/monty/README.md);
it is reached server-side only.

| Key | Default | Notes |
|-----|---------|-------|
| `BaseUrl` | `http://localhost:8077` | Where the monty sidecar listens. |
| `RequestTimeoutSeconds` | `30` | HTTP backstop above the run's wall clock, for a hung sidecar. Must be strictly greater than `Gert:Sandbox:WallClockSeconds`; enforced at startup when the monty backend is selected. |

---

## 8. `Gert:Extractor` — isolated document extraction

Binds to `ExtractorOptions`. The pdf/docx text extractor runs as an unprivileged,
rlimit-capped helper process.

| Key | Default | Notes |
|-----|---------|-------|
| `HelperPath` | `gert-extract` | The helper executable. |
| `AddressSpaceMiB` | `512` | RLIMIT_AS cap. |
| `CpuSeconds` | `20` | RLIMIT_CPU cap. |
| `ProcessLimit` | `16` | RLIMIT_NPROC cap. |
| `WallClockSeconds` | `30` | Kill timeout backstopping RLIMIT_CPU. |

---

## 9. `Gert:Turn` — the detached turn pipeline

Binds to `TurnOptions` (`src/Gert.Service/Chat/TurnOptions.cs`). A **round** is one
upstream completion request that comes back with tool calls — executing them and
re-prompting starts the next round, so every round costs a full vLLM completion.

The guards are layered (the rationale and survey live in
[design/turn-budgets.md](../design/turn-budgets.md)): bound every part, brake the loop,
make every trip visible on its tool card.

| Key | Default | Notes |
|-----|---------|-------|
| `MaxTurnDuration` | `00:05:00` | Hard wall-clock cap on one turn (model rounds + tools) — the real budget. Doubles as the orphan horizon: a `streaming` row older than this reads as `error`. |
| `MaxConcurrentTurns` | `4` | Parallel turn lanes. Turns shard by (user, project, conversation): one conversation never runs concurrently with itself; different conversations may overlap. `1` restores the global serial worker. Must be ≥ 1 (validated at startup). |
| `MaxToolRounds` | `64` | **Runaway brake, not a work budget** — sized an order of magnitude above legitimate turns. Past it the runner refuses further calls with budget-exhausted errors (visible on the cards), winds down in one final round, and logs a warning. |
| `MaxTokensPerRound` | `16384` | Per-round completion bound: the `max_tokens` sent on every upstream request. Both the default (when neither the conversation nor the user's per-model settings ask) and the ceiling (requested values clamp down). Reasoning tokens count against it on thinking models — keep it generous. `0` disables. |
| `ToolCallTimeout` | `00:01:00` | Generic wall-clock backstop on one tool execution, behind each tool's own tighter limits (sandbox wall clock, search timeouts). A trip fails that call with a visible card error; the turn continues. `0` disables. Interactive tools (`ask_user`) are exempt — see `AskUserTimeout`. |
| `AskUserTimeout` | `00:05:00` | How long one `ask_user` question waits for the user before the tool returns its graceful "user did not respond" result. The effective wait is min(this, remaining turn budget − 15 s grace), so it can never outlive `MaxTurnDuration`. |
| `DeltaFlushInterval` | `00:00:00.150` | Delta coalescing window — buffered model chunks emit as one event per window. `0` disables coalescing. |
| `DeltaFlushMaxChars` | `512` | Size backstop for the coalescing window. |

The per-round `max_tokens` is also user-facing: the model picker's cogwheel sets
per-model defaults, and conversation params override those — the server value above is
the cascade's last fallback *and* its ceiling.

---

## 10. `Artifacts` — served-artifact tickets

Binds to `ArtifactTicketOptions` (`src/Gert.Api/Security/ArtifactTicketOptions.cs`):
the separate-origin HTML-artifact preview and the HMAC-signed capability URLs it rides
(security F3). Top-level section, like `Auth`/`Storage`.

| Key | Default | Notes |
|-----|---------|-------|
| `Origin` | *(empty)* | The separate origin (`scheme://host[:port]`) that serves rendered HTML artifacts — a sandbox subdomain in prod, a second port in dev/CI. Empty means same origin: the ticket URL is relative and isolation rests on the iframe sandbox alone. |
| `Secret` | *(unset)* | **Secret** — env / user-secrets only. HMAC signing key for ticket URLs. An explicit value must be at least **32 UTF-8 bytes** (e.g. `openssl rand -base64 32`) — the host refuses to start on a shorter one. Unset = a random per-process key: fine for a single instance, but tickets stop surviving restarts and multiple instances behind a LB won't accept each other's tickets. |
| `Lifetime` | `00:05:00` | Ticket validity window. Long enough to load the iframe, short enough that a leaked URL is near-useless. |

---

## 11. `Gert:RateLimiting` — the per-user API limiter

Binds to `RateLimiting.PolicyOptions` (`src/Gert.Api/Security/RateLimiting.cs`),
security F10. A **fixed window per user**: each authenticated caller gets its own
partition keyed by the token `(iss, sub)` pair — the same identity anchor as the user
folder key, so two IdPs minting the same `sub` never share a bucket (anonymous traffic
falls back to the remote IP). This means
one client — or one stolen token — can't saturate the box, and one user's bursts never
throttle another's. Applied to `/api/*` only; `/healthz` is exempt. A rejected request
is a branded `429`. The defaults are a DoS brake, not a usage quota — leave the section
absent and nothing changes.

| Key | Default | Notes |
|-----|---------|-------|
| `PermitLimit` | `600` | Max requests per partition (per user / per anonymous IP) within one window. |
| `Window` | `00:01:00` | The fixed window length. |

---

## 12. Request size limits

Not knobs — compile-time constants, listed so the numbers are findable:

- **Document uploads are capped at 50 MiB** (`UploadConstraints.MaxSizeBytes`), enforced
  fail-closed by `DocumentUploadValidator` and re-checked on the streamed bytes, so an
  over-limit upload gets the branded 400.
- **Kestrel's request-body limit is set to that cap + 1 MiB** of multipart-framing
  headroom (`src/Gert.Api/Program.cs`), so a full-size file reaches the validator
  instead of dying as a bare Kestrel 413.

> **Reverse proxy:** a proxy in front needs a matching body-size setting — e.g. nginx
> `client_max_body_size 51m;` — or it will reject big uploads before Gert sees them.

---

## 13. Dev & test modes

Not for production — listed here so a deployment never enables them by accident.

| Switch | What it does |
|--------|--------------|
| `Gert:Dev:JwksPath` | Trust a local dev JWKS (offline token mint). Refused in Production. |
| `Gert:Web:TestHarness` | Serves the component-test harness pages from the SPA origin. Off unless explicitly `true`. |
| `make run` | Plain host against whatever appsettings says. |
| `make serve-mock` | Everything mocked (auth, vLLM, SearXNG) + a dev proxy that signs you in — no real upstreams needed. |
| `make serve-mock-vllm VLLM_URL=…` | Same mocked world, but chat hits a **real** vLLM; model context + tools/vision are probed and injected automatically. Add `SEARXNG_URL=` (instance must allow `format=json`) to make web search real too. `VLLM_MODEL=` restricts to one id, `ROLE=` picks the identity. |

The `FakeE2E` launch profile (`src/Gert.Api/Properties/launchSettings.json`) is the
glue the harness uses: real adapters, mock URLs, dev JWKS. It is a Development-only
profile by construction.
