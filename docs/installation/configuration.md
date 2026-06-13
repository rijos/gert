# Configuring Gert

The operator's reference: every server-level knob, where it binds from, and a working
example for each. This is the **installation** view - the *design* of the configuration
cascade (server -> user -> project -> conversation) and the project model live in
[design/configuration.md](../design/configuration.md).

---

## 1. How configuration binds

Standard ASP.NET Core layering, last one wins:

```
appsettings.json  ->  appsettings.{Environment}.json  ->  environment variables  ->  command line
```

- **appsettings.json** (`src/Gert.Api/appsettings.json`) - non-secret defaults. This is
  where a deployment's static config lives.
- **Environment variables** - replace `:` with `__`:
  `Gert__OpenAI__BaseUrl=http://vllm:8000`. Map entries by key:
  `Gert__Providers__qwen36-thinking__Parameters__Model=qwen36`.
- **Command line** - `dotnet run --project src/Gert.Api -- --Gert:OpenAI:BaseUrl=http://vllm:8000`.
  Highest precedence; beats launch-profile environment variables too.

**Secrets never go in appsettings.json** (security F8). A bearer key, if your upstream
needs one, arrives via an environment variable or `dotnet user-secrets`. Chat keys are
**per provider** (`Gert:Providers:<slug>:Parameters:ApiKey`); the embeddings key is
`Gert:OpenAI:ApiKey`:

```bash
dotnet user-secrets --project src/Gert.Api set "Gert:Providers:qwen36-thinking:Parameters:ApiKey" "sk-..."
dotnet user-secrets --project src/Gert.Api set "Gert:OpenAI:ApiKey" "sk-..."
# or
export Gert__Providers__qwen36-thinking__Parameters__ApiKey=sk-...
export Gert__OpenAI__ApiKey=sk-...
```

---

## 2. Minimal working example

A single vLLM box serving one chat model plus the embedding model. Chat connection +
sampling live under `Gert:Providers` (one named preset per picker entry); `Gert:OpenAI`
is now just the embeddings upstream and the shared chat-transport resilience defaults:

```jsonc
{
  "Gert": {
    "Providers": {
      "qwen36": {                                 // map key = the provider slug (GET /api/models id)
        "Name": "Qwen 3.6 27B",
        "Type": "openai",                         // selects the chat-client impl (openai = OpenAI-compatible/vLLM)
        "Default": true,
        "Capabilities": [ "tools", "vision" ],
        "Context": 131072,
        "Parameters": {
          "BaseUrl": "http://vllm-host:8000", // NO trailing /v1 - Gert appends /v1/... itself
          "Model": "qwen36"                       // the upstream model id (sent as `model`)
          // ApiKey is a SECRET - env / user-secrets only (section 1), never here
        }
      }
    },
    "OpenAI": {
      "BaseUrl": "http://vllm-host:8000",     // embeddings upstream, NO trailing /v1
      "EmbeddingModelId": "bge-m3",
      "EmbeddingDimensions": 1024
    },
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

> **The `/v1` gotcha:** every base URL here - a provider's
> `Parameters:BaseUrl` and `Gert:OpenAI:BaseUrl` - is the *server* base
> (`http://host:8000`), not the OpenAI API base (`http://host:8000/v1`). The adapter
> appends `/v1` itself - and tolerates a pasted `/v1` suffix (it is normalized, never
> doubled up).

---

## 3. `Gert:OpenAI` - the embeddings upstream + shared chat resilience

The **embeddings** connection plus the resilience defaults the **shared chat
transport** reuses. Chat *connection + sampling* now live per provider in
[`Gert:Providers`](#4-gertproviders---the-chat-provider-catalog) (this section no
longer carries a chat model id). Binds to `OpenAIOptions`
(`src/Gert.External/OpenAI/OpenAIOptions.cs`).

| Key | Default | Notes |
|-----|---------|-------|
| `BaseUrl` | `http://localhost:8000` | Embeddings server base URL, **without** `/v1`. |
| `ApiKey` | *(unset)* | **Secret** - env / user-secrets only. The **embeddings** bearer key, sent as `Authorization: Bearer`. (Each chat provider carries its own key under `Gert:Providers:<slug>:Parameters:ApiKey`.) |
| `EmbeddingModelId` | `bge-m3` | Sent as `model` on `/v1/embeddings`. Knowledge upload (RAG) fails if the upstream doesn't serve it. |
| `EmbeddingDimensions` | `1024` | Must match the embedding model (bge-m3 = 1024). Effectively immutable once data exists - it bakes into every `rag.db` ([design section 4](../design/configuration.md)). |
| `RequestTimeoutSeconds` | `120` | Max wait, **per attempt**, for an upstream to *accept* a request (time to response headers) - **not** the stream duration; the chat stream is bounded by the turn budget ([section 9](#9-gertturn---the-detached-turn-pipeline)). Shared by chat and embeddings. |
| `RetryCount` | `2` | Retries on transient pre-stream (connect/headers) failures, for chat and embeddings. Safe for chat - a retried attempt means no tokens were streamed. `0` disables. |

> **vLLM prefix caching.** Gert's requests are built to be prefix-cache friendly:
> the system prompt is static per project, history is replayed verbatim, tool specs
> serialize deterministically, and no per-request unique fields are sent. To benefit,
> make sure the vLLM server has automatic prefix caching enabled - it is **on by
> default in the V1 engine**; verify with `--enable-prefix-caching` (and watch the
> `vllm:prefix_cache_hit_rate` metrics). Two caveats: prefix reuse is shared across
> all users of the server (fine for a single-tenant box; pass a per-tenant
> `cache_salt` upstream if you ever need isolation), and editing a project's pinned
> instructions invalidates every conversation prefix in that project.

---

## 4. `Gert:Providers` - the chat provider catalog

A **map keyed by provider slug** - each entry is one named chat preset behind Gert's
chat abstraction, and the slug is the `id` that `GET /api/models` publishes to the
picker (in configured/document order). Binds to `ChatProviderOptions`
(`src/Gert.External/Providers/ChatProviderOptions.cs`). When the section is absent or
empty, the catalog falls back to a **single default `openai` provider** built from
`Gert:OpenAI:BaseUrl` (upstream model `default`), so the picker always has one real
option; an operator who configures `Gert:Providers` takes over completely.

The split is **catalog/connection on the entry, sampling under `Parameters`:**

| Key | Required | Notes |
|-----|----------|-------|
| `Name` | no | Display name in the picker. Defaults to the slug. |
| `Type` | no | Selects the chat-client implementation. `"openai"` (default) is the only one implemented - an OpenAI-compatible / vLLM endpoint; the schema is open for others (`"anthropic"`, ...). |
| `Default` | no | The server-level default of the config cascade - the picker's initial selection. Flag exactly one. |
| `Capabilities` | no | Capability tokens, shown as badges. `"tools"` is **load-bearing**: it gates tool calling. **Unset (null) means permissive** - the provider is assumed tool-capable; an explicit list *without* `"tools"` (e.g. `["text only"]`) disables tools for that provider. Other tokens (`"vision"`, ...) are display-only today. |
| `Context` | no | Context window in tokens - the "128K ctx" badge. vLLM reports it as `max_model_len` on `GET /v1/models`. Unset hides the badge. |
| `Fast` | no | Display-only "- fast" marker. |
| `Parameters` | yes | The `Type`-specific connection + sampling bag - see below. (The picker's `endpoint` hint is taken from `Parameters.BaseUrl`.) |

### 4a. `Parameters` - connection + sampling (the `openai` type)

Binds to `ChatProviderParameters` (`src/Gert.External/Providers/ChatProviderParameters.cs`).
The OpenAI REST-spec sampling fields are typed and **all optional - null omits the
field, so the upstream's own default applies**; everything *outside* the spec rides
`Extra`.

| Key | Default | Notes |
|-----|---------|-------|
| `BaseUrl` | `http://localhost:8000` | Server base URL, **without** `/v1`. |
| `Model` | `default` | The upstream model id, sent as `model` on `/v1/chat/completions`. Must exist on the server (`GET <BaseUrl>/v1/models`). |
| `ApiKey` | *(unset)* | **Secret** (F8) - env / user-secrets only, **never appsettings.json**. Sent as `Authorization: Bearer`. Empty for a keyless vLLM. |
| `Temperature` `TopP` `PresencePenalty` `FrequencyPenalty` `Seed` `Stop` | *(unset)* | Typed OpenAI-spec sampling. Each unset field is **omitted** from the request, so the upstream default stands. |
| `Extra` | `{}` | A **string->string map** for everything *outside* the OpenAI REST spec, keyed by JSON path under the request root (dotted, `$.` is prepended at apply time), value parsed to its JSON type. This is where the **vLLM extensions** (`top_k`, `min_p`, `repetition_penalty`) and the **template kwargs** (`chat_template_kwargs.enable_thinking`, `chat_template_kwargs.preserve_thinking`) live. |

### 4b. Thinking vs instruct is a *provider* choice

There is no per-request or per-conversation thinking toggle - **you pick a thinking
provider or an instruct provider.** The same physical model appears under several slugs
with different sampling. The canonical Qwen 3.6 pair:

```jsonc
"Providers": {
  "qwen36-thinking": {
    "Name": "Qwen 3.6 - thinking",
    "Type": "openai",
    "Default": true,
    "Capabilities": [ "tools", "vision" ],
    "Context": 131072,
    "Parameters": {
      "BaseUrl": "http://vllm-host:8000",
      "Model": "qwen36",
      "Temperature": 0.6,
      "TopP": 0.95,
      "Extra": {
        "top_k": "20",
        "chat_template_kwargs.enable_thinking": "true"
      }
    }
  },
  "qwen36-instruct": {
    "Name": "Qwen 3.6 - instruct",
    "Type": "openai",
    "Capabilities": [ "tools", "vision" ],
    "Context": 131072,
    "Parameters": {
      "BaseUrl": "http://vllm-host:8000",
      "Model": "qwen36",                                  // same upstream model, different preset
      "Temperature": 0.7,
      "TopP": 0.8,
      "PresencePenalty": 1.5,
      "Extra": {
        "top_k": "20",
        "chat_template_kwargs.enable_thinking": "false"
      }
    }
  }
}
```

`chat_template_kwargs.preserve_thinking` (interleaved thinking - replaying prior
`reasoning_content` upstream) is a third `Extra` flag, set on a thinking provider that
wants its earlier reasoning fed back. The **response-side** reasoning bubble streams and
persists regardless of provider ([chat-and-tools](../design/chat-and-tools.md#chat-orchestration-the-tool-loop)).

Filling in `Context` and `Capabilities` is on you, the operator - Gert binds this
statically at startup and never probes the upstream. Two quick ways to find the values:

```bash
# context window:
curl -s http://vllm-host:8000/v1/models | jq '.data[] | {id, max_model_len}'

# tools / vision: the dev harness probes them and prints a line per model at boot -
make serve-mock-vllm VLLM_URL=http://vllm-host:8000/v1
#   qwen36: 131072 ctx, tools/vision
```

Users pick from this catalog; they can never add endpoints or model ids of their own
([design section 4](../design/configuration.md#4-llm-providers--models)).

---

## 5. `Auth` + `Storage` - identity and the data root

| Key | Notes |
|-----|-------|
| `Auth:Authority` | The OIDC issuer (Pocket ID). JWTs are validated against its JWKS, RS256 only. |
| `Auth:Audience` | Expected `aud` claim, e.g. `gert-api`. |
| `Storage:DataRoot` | Filesystem root holding the `users/` tree. Everything Gert stores lives here; back up this directory and you've backed up Gert. |
| `Storage:ExpectedIssuer` | Fail-closed `iss` assertion checked **before** any user folder is created (F12). Normally equal to `Auth:Authority`. |

Both `Storage` values are **required** - the host refuses to start without them.

Dev-only escape hatch: `Gert:Dev:JwksPath` points at a local JWKS file so the test
harness can mint tokens offline. It is rejected in the Production environment; never
set it on a real deployment.

---

## 6. `Gert:Search` - SearXNG web search

Binds to `SearXngOptions`. The fetch step (downloading result pages) is the
SSRF-exposed part and is **off by default**. The `web_fetch` tool shares this
section's fetch caps (`MaxFetchBytes` / `FetchTimeoutSeconds` / `MaxRedirects`)
- same guarded fetcher, no parallel knob set; it needs no SearXNG instance and
ignores `BaseUrl` / `FetchPages`.

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

## 7. `Gert:Sandbox` - the `run_python` sandbox

Binds to `PythonSandboxOptions`. Two backends sit behind one `IPythonSandbox` port; `Backend`
picks. The defaults *are* the security posture: egress off, no `/data` mount, hard
caps. Raise them knowingly.

| Key | Default | Notes |
|-----|---------|-------|
| `Backend` | `monty` | Which backend runs `run_python`: `monty` (Pydantic's Rust Python interpreter via the sidecar - no container infra) or `gvisor` (runsc container). An unknown value fails fast at startup. |
| `WallClockSeconds` | `10` | Kill timeout per run (both backends). |
| `MemoryMiB` | `256` | Memory limit (both backends). |
| `MaxOutputBytes` | `65536` | Captured stdout/stderr cap (both backends). |
| `CpuSeconds` | `5` | CPU-time limit (gVisor only). |
| `PidLimit` | `64` | Max processes/threads (gVisor only). |
| `TmpSizeMiB` | `32` | Writable `/tmp`; rootfs stays read-only (gVisor only). |
| `RunscPath` | `runsc` | Path to the gVisor binary (gVisor only). |
| `Image` | `gert-sandbox-python` | OCI bundle with a Python runtime (gVisor only). |
| `EgressEnabled` | `false` | gVisor outbound network - the exfiltration brake. Leave off unless you must. (Monty has no network at all.) |

### 7a. `Gert:Sandbox:Monty` - the monty sidecar (`Backend=monty`)

Binds to `MontyOptions`. Run the sidecar from [tools/monty](../../tools/monty/README.md);
it is reached server-side only.

| Key | Default | Notes |
|-----|---------|-------|
| `BaseUrl` | `http://localhost:8077` | Where the monty sidecar listens. |
| `RequestTimeoutSeconds` | `30` | HTTP backstop above the run's wall clock, for a hung sidecar. Must be strictly greater than `Gert:Sandbox:WallClockSeconds`; enforced at startup when the monty backend is selected. |

---

## 8. `Gert:Extractor` - isolated document extraction

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

## 9. `Gert:Turn` - the detached turn pipeline

Binds to `TurnOptions` (`src/Gert.Service/Chat/TurnOptions.cs`). A **round** is one
upstream completion request that comes back with tool calls - executing them and
re-prompting starts the next round, so every round costs a full vLLM completion.

The guards are layered (the rationale and survey live in
[design/turn-budgets.md](../design/turn-budgets.md)): bound every part, brake the loop,
make every trip visible on its tool card.

| Key | Default | Notes |
|-----|---------|-------|
| `MaxTurnDuration` | `00:05:00` | Hard wall-clock cap on one turn (model rounds + tools) - the real budget. Doubles as the orphan horizon: a `streaming` row older than this reads as `error`. |
| `MaxConcurrentTurns` | `4` | Parallel turn lanes. Turns shard by (user, project, conversation): one conversation never runs concurrently with itself; different conversations may overlap. `1` restores the global serial worker. Must be >= 1 (validated at startup). |
| `MaxToolRounds` | `64` | **Runaway brake, not a work budget** - sized an order of magnitude above legitimate turns. Past it the runner refuses further calls with budget-exhausted errors (visible on the cards), winds down in one final round, and logs a warning. |
| `MaxSearchCallsPerTurn` | `5` | Per-turn cap on `web_search` calls - searches dominate tool-loop runaway (each costs a SearXNG round-trip and floods the prompt with results). Past it, further searches fail with a budget-exhausted error the model can read (visible on the card); the turn continues. `0` disables. |
| `MaxTokensPerRound` | `16384` | Per-round completion bound: the `max_tokens` sent on every upstream request. Reasoning tokens count against it on a thinking provider - keep it generous. `0` disables. |
| `ToolCallTimeout` | `00:01:00` | Generic wall-clock backstop on one tool execution, behind each tool's own tighter limits (sandbox wall clock, search timeouts). A trip fails that call with a visible card error; the turn continues. `0` disables. Interactive tools (`ask_user`) are exempt - see `AskUserTimeout`. |
| `AskUserTimeout` | `00:05:00` | How long one `ask_user` question waits for the user before the tool returns its graceful "user did not respond" result. The effective wait is min(this, remaining turn budget - 15 s grace), so it can never outlive `MaxTurnDuration`. |
| `DeltaFlushInterval` | `00:00:00.150` | Delta coalescing window - buffered model chunks emit as one event per window. `0` disables coalescing. |
| `DeltaFlushMaxChars` | `512` | Size backstop for the coalescing window. |

`MaxTokensPerRound` is the single per-round `max_tokens` for every turn - sampling is no
longer a user/conversation cascade (it rides the selected provider -
[section 4](#4-gertproviders---the-chat-provider-catalog)), so there is no per-model cogwheel to
override it.

---

## 10. `Artifacts` - served-artifact tickets

Binds to `ArtifactTicketOptions` (`src/Gert.Api/Security/ArtifactTicketOptions.cs`):
the separate-origin HTML-artifact preview and the HMAC-signed capability URLs it rides
(security F3). Top-level section, like `Auth`/`Storage`.

| Key | Default | Notes |
|-----|---------|-------|
| `Origin` | *(empty)* | The separate origin (`scheme://host[:port]`) that serves rendered HTML artifacts - a sandbox subdomain in prod, a second port in dev/CI. Empty means same origin: the ticket URL is relative and isolation rests on the iframe sandbox alone. |
| `Secret` | *(unset)* | **Secret** - env / user-secrets only. HMAC signing key for ticket URLs. An explicit value must be at least **32 UTF-8 bytes** (e.g. `openssl rand -base64 32`) - the host refuses to start on a shorter one. Unset = a random per-process key: fine for a single instance, but tickets stop surviving restarts and multiple instances behind a LB won't accept each other's tickets. |
| `Lifetime` | `00:05:00` | Ticket validity window. Long enough to load the iframe, short enough that a leaked URL is near-useless. |

---

## 11. `Gert:RateLimiting` - the per-user API limiter

Binds to `RateLimiting.PolicyOptions` (`src/Gert.Api/Security/RateLimiting.cs`),
security F10. A **fixed window per user**: each authenticated caller gets its own
partition keyed by the token `(iss, sub)` pair - the same identity anchor as the user
folder key, so two IdPs minting the same `sub` never share a bucket (anonymous traffic
falls back to the remote IP). This means
one client - or one stolen token - can't saturate the box, and one user's bursts never
throttle another's. Applied to `/api/*` only; `/healthz` is exempt. A rejected request
is a branded `429`. The defaults are a DoS brake, not a usage quota - leave the section
absent and nothing changes.

| Key | Default | Notes |
|-----|---------|-------|
| `PermitLimit` | `600` | Max requests per partition (per user / per anonymous IP) within one window. |
| `Window` | `00:01:00` | The fixed window length. |

---

## 12. Request size limits

Not knobs - compile-time constants, listed so the numbers are findable:

- **Document uploads are capped at 50 MiB** (`UploadConstraints.MaxSizeBytes`), enforced
  fail-closed by `DocumentUploadValidator` and re-checked on the streamed bytes, so an
  over-limit upload gets the branded 400.
- **Kestrel's request-body limit is set to that cap + 1 MiB** of multipart-framing
  headroom (`src/Gert.Api/Program.cs`), so a full-size file reaches the validator
  instead of dying as a bare Kestrel 413.

> **Reverse proxy:** a proxy in front needs a matching body-size setting - e.g. nginx
> `client_max_body_size 51m;` - or it will reject big uploads before Gert sees them.

---

## 13. Dev & test modes

Not for production - listed here so a deployment never enables them by accident.

| Switch | What it does |
|--------|--------------|
| `Gert:Dev:JwksPath` | Trust a local dev JWKS (offline token mint). Refused in Production. |
| `Gert:Web:TestHarness` | Serves the component-test harness pages from the SPA origin. Off unless explicitly `true`. |
| `make run` | Plain host against whatever appsettings says. |
| `make serve-mock` | Everything mocked (auth, vLLM, SearXNG) + a dev proxy that signs you in - no real upstreams needed. |
| `make serve-mock-vllm VLLM_URL=...` | Same mocked world, but chat hits a **real** vLLM; model context + tools/vision are probed and injected automatically. Boots the host at `Debug` log level ([section 14](#14-logging---verbosity)). Add `SEARXNG_URL=` (instance must allow `format=json`) to make web search real too. `VLLM_MODEL=` restricts to one id, `ROLE=` picks the identity. |

The `FakeE2E` launch profile (`src/Gert.Api/Properties/launchSettings.json`) is the
glue the harness uses: real adapters, mock URLs, dev JWKS. It is a Development-only
profile by construction.

---

## 14. `Logging` - verbosity

The standard .NET `Logging:LogLevel` config drives **Serilog** (the host logger): set the
floor with `Logging:LogLevel:Default`, and quiet individual categories with overrides.

```jsonc
"Logging": {
  "LogLevel": {
    "Default": "Information",          // Trace|Debug|Information|Warning|Error|Critical
    "Microsoft.AspNetCore": "Warning"  // keep framework noise down, even at Default=Debug
  }
}
```

Bind it like any other knob ([section 1](#1-how-configuration-binds)) - appsettings, the
`Logging__LogLevel__Default=Debug` env var, or a `--Logging:LogLevel:Default=Debug`
command-line override. `make serve-mock-vllm` uses the last form to boot at `Debug` so chat
behaviour (the turn pipeline + the HTTP traffic to vLLM) is visible, while the
`Microsoft.AspNetCore` override keeps Kestrel internals quiet. Levels map onto Serilog's
(`Trace`->`Verbose`, `Critical`->`Fatal`); the NDJSON `level` field is lower-cased
(`"level":"debug"`).

### Tracing the model wire (tuning)

At `Debug`, `OpenAIWireLogger` traces the **exact POST** Gert sends to the OpenAI-compatible
upstream - method, URI, headers (the api-key bearer **redacted**), and the full body
(messages, tools, sampling params, `chat_template_kwargs`, plus the SDK-injected
`model`/`stream`/`stream_options`), then the response status + headers. This is the seam for
tuning sampling and the tools block. Each trace is one NDJSON line tagged `OpenAI request:` /
`OpenAI response:` in `msg`, with the body also broken out as a `Body` field - so pull the
request bodies out cleanly with:

```bash
make serve-mock-vllm VLLM_URL=http://...:8000/v1 2>&1 \
  | jq -r 'select(.msg | startswith("OpenAI request")) | .Body' | jq .
```

> WARNING: The body carries message **content**, so this is a **local tuning mode only** - never
> run the host at `Debug` in production ([operations section logging format](../design/operations.md#logging-format-shared)).
> The response body is the SSE stream and is left untraced so streaming isn't buffered.
