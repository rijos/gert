# Authentication & authorization

## Token flow

The SPA is a **public OIDC client** using **Authorization Code + PKCE** against Pocket ID:

1. SPA redirects to Pocket ID for login.
2. Pocket ID returns an authorization code; SPA exchanges it (with PKCE verifier) for an **access token (JWT)** and a refresh token.
3. SPA sends `Authorization: Bearer <access-token>` on every API call.
4. SPA silently refreshes before expiry using the refresh token.

The API only ever needs Pocket ID's **JWKS endpoint** to validate signatures — it never sees credentials.

> **Client-side token storage.** The SPA keeps the access token **in memory only** (not
> `localStorage`), so injected script has nothing persistent to steal; signature validation is
> **pinned to `RS256`** to foreclose alg-confusion. See [security F2/F11](security.md#3-findings--remediations)
> and [ui-components](ui-components.md#security-token-handling--rendering).

> **Passkey-only login.** Pocket ID authenticates users with **passkeys (WebAuthn)** — there are no passwords. Onboarding is an admin step in Pocket ID: create the user and hand them a one-time setup link, which they open to register a passkey. This is transparent to Gert.Api — it only ever sees the resulting JWT — but it means there is no password or credential-reset flow for the API to handle. (Pocket ID has no forward-auth proxy mode, so the API always validates the JWT itself.)

## Expected JWT claims

| Claim | Example | Used for |
|-------|---------|----------|
| `sub` | `8f3a1c2e-…` (stable, opaque) | **User key** → folder name. Never changes. |
| `preferred_username` | `gerrit.de.vries` | Display name (the userchip). |
| `email` | `gerrit@homelab.lan` | Optional display / contact. |
| `groups` (or `roles`) | `["gert-users","gert-admins"]` | **Authorization** (admin policy). |
| `gert_tools` | `"rag search sandbox"` / `["rag","search"]` / `"*"` | **Tool entitlement** — which tools the user may invoke. See [Tool entitlements](#tool-entitlements-allowed-tools-in-the-jwt). |
| `iss` | `https://id.homelab.lan` | Validation. |
| `aud` | `gert-api` | Validation (this API's client id). |
| `exp` / `iat` / `nbf` | epoch seconds | Lifetime validation. |

> **`sub` vs `preferred_username` for the folder name.** Use `sub`. Usernames can be renamed in the IdP, which would orphan a folder; `sub` is stable for the life of the account. The human-readable username is stored inside the folder (`user.db`'s `user_meta` row, refreshed from the token when it changes) so admin tooling can map name → key. See [Operations → User lifecycle](operations.md#user-lifecycle--remove-a-user--remove-a-folder).

## ASP.NET Core wiring

```csharp
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = cfg["Auth:Authority"];   // Pocket ID issuer; fetches JWKS automatically
        o.Audience  = cfg["Auth:Audience"];    // "gert-api"
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms          = ["RS256"],   // pin: no alg-confusion / "none" (security F11)
            NameClaimType            = "preferred_username",
            RoleClaimType            = "groups",
            ClockSkew                = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("Admin", p => p.RequireRole("gert-admins"));
    // every non-admin endpoint just needs an authenticated user
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});
```

## The user context (resolved per request)

```csharp
public sealed class UserContext(IHttpContextAccessor http, ToolRegistry registry)
{
    private ClaimsPrincipal User => http.HttpContext!.User;

    public string Sub      => User.FindFirstValue("sub")
                              ?? throw new UnauthorizedAccessException("no sub claim");
    public string Iss      => User.FindFirstValue("iss")
                              ?? throw new UnauthorizedAccessException("no iss claim");
    public string Username => User.Identity?.Name ?? Sub;
    public bool   IsAdmin  => User.IsInRole("gert-admins");

    // Hard ceiling on which tools the model may call for this user (see "Tool entitlements").
    // The JWT is the SOLE source — no default grant; an absent claim means no tools.
    public IReadOnlySet<string> AllowedTools
    {
        get
        {
            var raw = User.FindFirstValue("gert_tools");    // "rag search sandbox" | "*" | absent
            if (string.IsNullOrWhiteSpace(raw)) return EmptySet;        // claim absent → fail-closed
            if (raw.Trim() == "*")              return registry.AllIds; // blanket grant
            return registry.Normalize(raw);                 // delimited scope string ∩ registry
        }
    }
    public bool CanUseTool(string id) => AllowedTools.Contains(id);
}
```

The validated **`(iss, sub)`** pair is the only thing that ever decides which folder is touched — and
only after the fail-closed provisioning gate accepts it.

> **Identity is validated before any disk access.** The provisioning gate
> ([storage-and-data](storage-and-data.md#lazy-provisioning--migrations)) asserts `iss` ==
> the configured authority, `aud` == `gert-api`, and a well-formed `sub` **before** deriving a path
> or creating a directory — no folder is ever created for an unvalidated token. The folder key is
> `sha256(iss + sub)`, anchored on `sub` because it is the IdP's **stable, never-recycled** UUID —
> email is mutable and *recycled* (a reassigned address would inherit the prior owner's data) and so
> is rejected as the anchor ([decisions §3](decisions.md#3-folder-key)). Past that gate the
> validated JWT is trusted: the folder key derives from the token and nothing else; the username
> row in each folder's `user.db` is descriptive (admin key→user mapping), never a per-request
> check ([security F12](security.md#3-findings--remediations)).

## Authorization matrix

Three independent things decide access:

- **Authentication** — is there a valid, unexpired bearer token? (anonymous vs. authenticated)
- **Role** — the `groups` claim: `gert-users` (everyone) vs. `gert-admins` (the admin surface).
- **Data scope** *(not a role)* — every data endpoint is structurally bound to the caller's own `sub`-folder ([principle #3](principles.md)). No API path — admin included — reads another user's conversations or documents. The `{pid}` in project-scoped paths is request-supplied but resolves only *within* that `sub`-folder, so it scopes among the caller's own projects and never widens access ([configuration.md §2.5](configuration.md#25-path-resolution--why-a-request-supplied-project-id-is-still-idor-safe)).

| Endpoint(s) | Anonymous | User (`gert-users`) | Admin (`gert-admins`) |
|---|:---:|:---:|:---:|
| `GET /healthz` · `GET /readyz` | ✅ | ✅ | ✅ |
| `GET /api/models` | ❌ | ✅ | ✅ |
| `GET /api/settings` · `…/api/projects*` (list · create · read · update · delete) | ❌ | ✅ own | ✅ own |
| `…/api/projects/{pid}/conversations*` (list · create · read · update · delete) | ❌ | ✅ own | ✅ own |
| `POST /api/projects/{pid}/conversations/{id}/messages` (stream) | ❌ | ✅ own · tools gated by entitlement | ✅ own · entitlement |
| `…/api/projects/{pid}/documents*` · `…/memory*` (list · upload · poll · delete) | ❌ | ✅ own | ✅ own |
| `…/api/projects/{pid}/…/artifacts` · `…/export` · `DELETE /api/account` | ❌ | ✅ own | ✅ own |
| `GET /api/admin/users` | ❌ | ❌ | ✅ |
| `DELETE /api/admin/users/{key}` | ❌ | ❌ | ✅ |

- **✅ own** = served strictly from the caller's `sub`-folder. The user and admin columns are identical for data endpoints because **admin status grants no cross-user data read** — admin power is confined to the two `/api/admin/*` endpoints, which only read each folder's `user.db` username row (the footprint scan) and `rm -rf` a directory; they never open another user's `chat.db`/`rag.db`.
- **Anonymous** = missing / invalid / expired token → `401` (only the `GET /healthz` / `GET /readyz` probes are open).
- Enforced by the `FallbackPolicy` (authenticated-user) on everything, the `Admin` policy on `/api/admin/*`, and `sub`-folder resolution for data scope.
- The messages endpoint carries the extra **tool-entitlement** dimension below.

## Tool entitlements (allowed tools in the JWT)

Which **tools** a user may invoke is an admin-controlled entitlement carried in the token — not a per-user flag in the API (there is still no user table). The mechanism is deliberately generic: the API owns a small **tool registry**; the JWT lists the capability ids a user is granted; adding a new tool means extending the registry and granting its id — **no schema change and no per-tool code branch**.

### The `gert_tools` claim

The claim is the **sole source** of tool entitlement — there is no server-side default grant.
The admin sets a custom claim in Pocket ID, per user or per user-group:

| Claim value | Meaning |
|---|---|
| `"rag search"` (space/comma-delimited scope string) | grant exactly these tool ids |
| `"*"` | grant every tool in the registry — current **and future** (blanket grant) |
| *absent / blank* | **no tools** — fail-closed; every capability must be granted explicitly |

A bare login with no `gert_tools` claim is a working chat with **zero tools** (plain
completion, no RAG/search/canvas/etc.). This is deliberate: one rule, no exceptions —
capability comes from the token or not at all. (The claim is a **scope string**; the older
JSON-array form `["rag","search"]` is no longer parsed — its tokens match no registered id
and yield nothing.)

> **Pocket ID setup.** Define `gert_tools` as a custom claim and attach it to users or to a group (e.g. a `gert-tools` group granting `rag search todo clock make_artifact edit_artifact read_artifact ask_user fetch memory`, and a separate `gert-sandbox` group adding `sandbox`). Make sure it is emitted into the **access token** the API validates. If your Pocket ID build only places custom claims in the ID token / userinfo, have the API read it from the userinfo endpoint once per session — the rest of the logic is unchanged.

### Tool registry

Every id below must be **named in `gert_tools`** (or covered by `"*"`) to be usable — the
"granted" / "denied" distinction of earlier drafts is gone, because there is no default set
to be in or out of. The Notes column records why a grant is more or less sensitive:

| Tool (model function) | Capability id | Notes |
|---|---|---|
| RAG — `search_documents` | `rag` | reads **this** user's `rag.db` only |
| Web search — `web_search` | `search` | SearXNG; outbound egress |
| Sandbox — `run_python` | `sandbox` | gVisor; **executes arbitrary code** — the most sensitive grant, hand it out deliberately |
| Todos — `set_todos` | `todo` | renders the chat checklist; no external world |
| Clock — `get_datetime` | `clock` | reads the host clock via `TimeProvider`; no external world |
| Canvas create — `make_artifact` | `make_artifact` | writes this conversation's `artifacts` rows; no external world ([chat-and-tools](chat-and-tools.md#artifacts-the-canvas-tool-suite)) |
| Canvas edit — `edit_artifact` | `edit_artifact` | exact-substring replace on an existing artifact |
| Canvas read — `read_artifact` | `read_artifact` | read-only; returns numbered lines |
| Ask the user — `ask_user` | `ask_user` | blocks the turn awaiting the user's answer (own timeout budget); no external world ([chat-and-tools](chat-and-tools.md#ask-the-user-ask_user)) |
| Web fetch — `web_fetch` | `fetch` | outbound egress to a **model-named URL**; SSRF-guarded — the same hardened fetcher as web search's page pulls ([chat-and-tools](chat-and-tools.md#web-fetch-web_fetch)) |
| Save memory — `save_memory` | `memory` | writes a new memory entry into this project's `rag.db` + object store; immediately retrievable by `search_documents` ([chat-and-tools](chat-and-tools.md#save-memory-save_memory)) |

The SPA exposes the canvas trio (`make_artifact` / `edit_artifact` / `read_artifact`) as
**one "Canvas" switch**, so a token granting the canvas should grant all three. An id in the
claim that names no registered tool is silently dropped (intersected with the registry), so a
typo fails closed rather than erroring the login.

### Enforcement — the claim is the ceiling

The conversation toggles and the request body are *user preferences*; the JWT entitlement is the *authorization boundary*. The orchestrator intersects all three before advertising tools to the model:

```csharp
var offered = requestedTools                   // from the message request body
    .Where(conversation.EnabledTools.Contains) // per-conversation preference
    .Where(user.CanUseTool)                    // ← HARD ceiling: JWT entitlement
    .ToList();
// a tool the user isn't entitled to is dropped even if the client asks for it
```

Flipping a UI toggle can therefore never escalate capability: a user without `sandbox` in `gert_tools` simply never has `run_python` advertised to the model, and a user with **no claim at all** gets nothing advertised. See [chat orchestration](chat-and-tools.md#chat-orchestration-the-tool-loop) for where this sits in the loop.

> **Optional `GET /api/capabilities`.** Returns e.g. `{ "tools": ["rag","search"], "isAdmin": false }` so the SPA can disable toggles the user can't use. This is **cosmetic** — the orchestrator filter above is the real boundary; the UI hint just avoids showing a control that would be silently dropped.
