# Authentication & authorization

## Token flow

The SPA is a **public OIDC client** using **Authorization Code + PKCE** against Pocket ID:

1. SPA redirects to Pocket ID for login.
2. Pocket ID returns an authorization code; SPA exchanges it (with PKCE verifier) for an **access token (JWT)** and a refresh token.
3. SPA sends `Authorization: Bearer <access-token>` on every API call.
4. SPA silently refreshes before expiry using the refresh token.

The API only ever needs Pocket ID's **JWKS endpoint** to validate signatures — it never sees credentials.

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

> **`sub` vs `preferred_username` for the folder name.** Use `sub`. Usernames can be renamed in the IdP, which would orphan a folder; `sub` is stable for the life of the account. Store the human-readable username inside the folder as metadata (`meta.json`) so admin tooling can map name → key. See [Operations → User lifecycle](operations.md#user-lifecycle--remove-a-user--remove-a-folder).

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
public sealed class UserContext(IHttpContextAccessor http, IOptions<ToolOptions> tools)
{
    private ClaimsPrincipal User => http.HttpContext!.User;

    public string Sub      => User.FindFirstValue("sub")
                              ?? throw new UnauthorizedAccessException("no sub claim");
    public string Username => User.Identity?.Name ?? Sub;
    public bool   IsAdmin  => User.IsInRole("gert-admins");

    // Hard ceiling on which tools the model may call for this user (see "Tool entitlements").
    public IReadOnlySet<string> AllowedTools
    {
        get
        {
            var raw = User.FindFirstValue("gert_tools");    // "rag search sandbox" | JSON array | "*"
            if (string.IsNullOrWhiteSpace(raw)) return tools.Value.DefaultGrant;  // claim absent
            if (raw.Trim() == "*")              return ToolRegistry.AllIds;       // blanket grant
            return ToolRegistry.Normalize(raw);             // parse (array or delimited) ∩ registry
        }
    }
    public bool CanUseTool(string id) => AllowedTools.Contains(id);
}
```

`Sub` is the only thing that ever decides which folder is touched.

## Authorization matrix

Three independent things decide access:

- **Authentication** — is there a valid, unexpired bearer token? (anonymous vs. authenticated)
- **Role** — the `groups` claim: `gert-users` (everyone) vs. `gert-admins` (the admin surface).
- **Data scope** *(not a role)* — every data endpoint is structurally bound to the caller's own `sub`-folder ([principle #3](principles.md)). No API path — admin included — reads another user's conversations or documents.

| Endpoint(s) | Anonymous | User (`gert-users`) | Admin (`gert-admins`) |
|---|:---:|:---:|:---:|
| `GET /healthz` | ✅ | ✅ | ✅ |
| `GET /api/models` | ❌ | ✅ | ✅ |
| `…/api/conversations*` (list · create · read · update · delete) | ❌ | ✅ own | ✅ own |
| `POST /api/conversations/{id}/messages` (stream) | ❌ | ✅ own · tools gated by entitlement | ✅ own · entitlement |
| `…/api/documents*` (list · upload · poll · delete) | ❌ | ✅ own | ✅ own |
| `GET /api/conversations/{id}/artifacts` · `GET /api/artifacts/{id}` | ❌ | ✅ own | ✅ own |
| `GET /api/admin/users` | ❌ | ❌ | ✅ |
| `DELETE /api/admin/users/{key}` | ❌ | ❌ | ✅ |

- **✅ own** = served strictly from the caller's `sub`-folder. The user and admin columns are identical for data endpoints because **admin status grants no cross-user data read** — admin power is confined to the two `/api/admin/*` endpoints, which only scan `meta.json` and `rm -rf` a directory; they never open another user's `chat.db`/`rag.db`.
- **Anonymous** = missing / invalid / expired token → `401` (only `GET /healthz` is open).
- Enforced by the `FallbackPolicy` (authenticated-user) on everything, the `Admin` policy on `/api/admin/*`, and `sub`-folder resolution for data scope.
- The messages endpoint carries the extra **tool-entitlement** dimension below.

## Tool entitlements (allowed tools in the JWT)

Which **tools** a user may invoke is an admin-controlled entitlement carried in the token — not a per-user flag in the API (there is still no user table). The mechanism is deliberately generic: the API owns a small **tool registry**; the JWT lists the capability ids a user is granted; adding a new tool means extending the registry and granting its id — **no schema change and no per-tool code branch**.

### The `gert_tools` claim

The admin sets a custom claim in Pocket ID, per user or per user-group:

| Claim value | Meaning |
|---|---|
| `"rag search"` (space-delimited) or `["rag","search"]` (array) | grant exactly these tool ids |
| `"*"` | grant every tool in the registry — current **and future** (blanket grant) |
| *absent* | fall back to the configured **default grant** (`Tools:DefaultGrant`, default `rag search`) |

> **Pocket ID setup.** Define `gert_tools` as a custom claim and attach it to users or to a group (e.g. a `gert-sandbox` group whose members get `sandbox`). Make sure it is emitted into the **access token** the API validates. If your Pocket ID build only places custom claims in the ID token / userinfo, have the API read it from the userinfo endpoint once per session — the rest of the logic is unchanged.

### Tool registry

| Tool (model function) | Capability id | Default if claim absent | Notes |
|---|---|:---:|---|
| RAG — `search_documents` | `rag` | granted | reads **this** user's `rag.db` only |
| Web search — `web_search` | `search` | granted | SearXNG; outbound egress |
| Sandbox — `run_python` | `sandbox` | **denied — opt-in** | gVisor; executes code, grant deliberately |

Sandbox defaults to *off* because it is the one tool that runs arbitrary code; it must be granted on purpose. All defaults are tunable via `Tools:DefaultGrant`.

### Enforcement — the claim is the ceiling

The conversation toggles and the request body are *user preferences*; the JWT entitlement is the *authorization boundary*. The orchestrator intersects all three before advertising tools to the model:

```csharp
var offered = requestedTools                   // from the message request body
    .Where(conversation.EnabledTools.Contains) // per-conversation preference
    .Where(user.CanUseTool)                    // ← HARD ceiling: JWT entitlement
    .ToList();
// a tool the user isn't entitled to is dropped even if the client asks for it
```

Flipping a UI toggle can therefore never escalate capability: a user without `sandbox` in `gert_tools` simply never has `run_python` advertised to the model. See [chat orchestration](chat-and-tools.md#chat-orchestration-the-tool-loop) for where this sits in the loop.

> **Optional `GET /api/capabilities`.** Returns e.g. `{ "tools": ["rag","search"], "isAdmin": false }` so the SPA can disable toggles the user can't use. This is **cosmetic** — the orchestrator filter above is the real boundary; the UI hint just avoids showing a control that would be silently dropped.
