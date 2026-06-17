# Configuration & projects

What a user can configure, and the **project** model that organises their data. This is the
feature layer on top of the storage foundation in [storage-and-data](storage-and-data.md) -
it extends the per-user folder with **per-project** folders and adds the settings that steer
chat, retrieval, language, and appearance. (For the *operator* view - every server-level
appsettings/env knob with defaults - see
[installation/configuration.md](../installation/configuration.md).)

> **One-line model:** a **project is its own scope** - its own conversations, documents, and
> memory, fully isolated from every other project. **"Default" is just the project you start
> in.** The same principle that isolates users isolates projects: a query is opened against
> *that project's* store, so it cannot reach another project's data. Deleting a project drops
> its stores.

This pushes [principle #2](principles.md) (the store scopes by token, not an application
filter) one level deeper, keeps [principle #5](principles.md) (deletion is a per-store erase,
not a row-scrub) intact at project scope, and gates every new setting through
[principle #6](principles.md) (fail-closed validation).

---

## 1. The configuration cascade

Settings resolve nearest-wins, from broadest to most specific:

```
  server / admin   ->   user   ->   project   ->   conversation
  (appsettings)        (you)      (a folder)     (one chat)
```

The **effective** value for any knob is the most specific one that is set; anything unset
inherits from the level above.

| Level | Owns | Stored | Set by |
|-------|------|--------|--------|
| **Server / admin** | provider catalog (connection + **sampling**, per named provider), embedding model, tool grants, caps (max upload) | `appsettings.json` ([tech-stack](tech-stack.md)) | operator only |
| **User** | theme, UI language, default reply language, default provider, default tools, memory mode | `user.db` `settings` row ([storage-and-data section user.db](storage-and-data.md#userdb)) | the user |
| **Project** | name, instructions, default provider, default tools, reply language | `user.db` `projects` registry row | the user |
| **Conversation** | provider, tools (per chat) | `conversations` row in that project's `chat.db` | the user, live (the model picker + tools menu) |

So picking a provider in the composer overrides the project default for that one
conversation; a project with no provider set inherits the user default; the user default
falls back to the server's flagged-default provider.

**Sampling is *not* a cascade level.** Temperature, top_p, the penalties, `top_k`, the
template kwargs - they live with the **provider** the picker selects
([section 4](#4-llm-providers--models), [installation section providers](../installation/configuration.md#4-gertchatproviders---the-chat-provider-catalog)),
not on the user, project, or conversation. What cascades is the **provider choice** (and
the tool toggles); the sampling rides whichever provider wins.

---

## 2. Projects

### 2.1 What a project is
A project is a self-contained workspace: a name, a few defaults, an optional **instructions**
block, and three data stores that are *only* ever opened for that project - conversations,
documents (RAG), and memory. Nothing crosses a project boundary. A user can have many; they
switch between them in the UI (the sidebar's project picker - [section 8](#8-the-spa-surface)).

### 2.2 "Default", not "global"
There is no global scope and no cross-project search. On first authenticated request the user
gets a **`default`** project (lazy provisioning, exactly like the user folder -
[storage-and-data](storage-and-data.md)). It is ordinary in every way except that it is the
landing project and is always present: the user can empty it, but the workspace never has zero
projects, so there is always somewhere to chat. Create another project and you've simply made a
second isolated folder.

### 2.3 Memory
Memory is **per project** - knowledge the assistant carries between conversations *within that
project*. Two mechanisms, by size and intent:

- **Instructions (pinned).** A small, always-injected block - the project's custom system
  prompt. Lives on the project's registry row in `user.db`, length-bounded
  ([section 6](#6-what-is-not-configurable) notes the cap). This is the cheap, deterministic
  "always know this" memory.
- **Memory entries (retrieved).** Markdown notes under `projects/{id}/memory/`, embedded into
  that project's `rag.db` alongside documents but tagged `kind='memory'`, so the
  `search_documents` tool can pull them when relevant ([chat-and-tools](chat-and-tools.md)). A
  `pinned` flag promotes a small entry into the always-injected set.

A user-level **memory mode** governs whether the assistant may *write* entries itself:
`off` - `manual` (only the user adds/edits) - `auto` (the model may append a "remember this"
entry). Default `manual`. Whichever the mode, memory is just files + RAG rows in the project
folder, so clearing it is a delete - no special machinery.

### 2.4 Storage model
A project's **data** is its folder; a project's **config** is a row in the user's registry:

```
/data/users/{key}/
  user.db                   # USER-level state: username (admin scan), settings (theme,
  │                         #   languages, defaults, memory mode), and the PROJECT REGISTRY -
  │                         #   one row per project: { id, name, description, instructions,
  │                         #   defaults (model_id?, tools?, reply_language?) }
  projects/
    default/                # lazily created; always present
      chat.db               #   conversations, messages, tool_calls, citations, artifacts  (this project)
      rag.db                #   documents, chunks, vec0, fts5  (this project)
      files/                #   original uploads for this project
      memory/               #   memory entries (markdown) -> embedded into this project's rag.db
    {project-id}/           # any further project - same shape, fully isolated
      ...
```

The project databases live **inside each project folder** - one `chat.db` + `rag.db` pair per
project, not per user ([storage-and-data](storage-and-data.md)). No `project_id` column in
them; the *path* is the scope. User settings and project config are rows in **`user.db`**
([storage-and-data section user.db](storage-and-data.md#userdb),
[decisions section 9](decisions.md#9-userdb---structured-user-state-is-a-database-not-json-sidecars))
- still inside the user's own folder, so the API owns nothing persistent of its own, and an
admin enumerates a user's projects by reading that user's registry.

Memory rides the document schema: `rag.db`'s `documents` table carries
`kind` (`document | memory`) and `pinned` (always-injected memory entries) -
see [storage-and-data section rag.db](storage-and-data.md#ragdb-sqlite-vec).

### 2.5 Path resolution & why a request-supplied project id is still IDOR-safe
`SqliteDatabasePaths` ([storage-and-data](storage-and-data.md)) gains a project segment:

```csharp
public string ProjectRoot(string iss, string sub, string projectId) =>
    Path.Combine(Root(iss, sub), "projects", projectId);   // Root(iss,sub) = users/{sha256(iss + sub)}
public string ChatDb(string iss, string sub, string projectId) => Path.Combine(ProjectRoot(iss, sub, projectId), "chat.db");
public string RagDb (string iss, string sub, string projectId) => Path.Combine(ProjectRoot(iss, sub, projectId), "rag.db");
```

The user key still comes **only** from the validated token `(iss, sub)` - anchored on the stable,
never-recycled `sub`
([principle #3](principles.md), [decisions section 3](decisions.md#3-user-key)). The project id
*does* come from the request - but it is validated to a safe shape (a UUID, or the literal
`default`) and is only ever joined **under the token-derived user folder**. So a tampered
project id can, at worst, reach *this same user's* other project or 404 - it can never escape
the user's directory. Cross-user IDOR remains structurally impossible; the project id selects
*within* an already-isolated folder, it does not widen it. Validation rejects non-UUID /
traversal values outright ([principle #6](principles.md)).

### 2.6 Retrieval is unchanged - just project-scoped
The tool loop and hybrid RAG in [chat-and-tools](chat-and-tools.md) are untouched except that
"this user's `rag.db`" becomes "this project's `rag.db`." No fusion across projects, no scope
flag - one corpus, the one the conversation lives in. Simpler than the perimeter it already had.

---

## 3. User settings

Stored as the single settings row in the user's `user.db`
([storage-and-data section user.db](storage-and-data.md#userdb)); edited via
`GET`/`PUT /api/settings` (`PUT` merges - each supplied field overrides, absent fields stay).

### 3.1 Theme
`light - dark - auto` - the two palettes are **Manila** (paper light) and **Ember** (refined
dark); `auto` follows the OS via `color-scheme`. Persisted **server-side** so it follows the
user across devices; the SPA still writes `localStorage` as a first-paint cache before settings
load, so there's no flash ([ui-components](ui-components.md#5-cross-cutting-concerns)). The
palettes themselves are fixed for v1 (a custom accent is a possible later addition -
[section 9](#9-open-decisions)).

### 3.2 Language
- **UI language** - the SPA's own strings, from a small per-locale JSON dictionary loaded by
  `state/ui.js` (no-npm i18n - [ui-components](ui-components.md)). Defaults from the browser's
  `Accept-Language`, then persisted.
- **Reply language** - `auto - <BCP-47>`. `auto` lets the model answer in the language of the
  message; a fixed value injects a "respond in X" instruction. Settable at user level and
  overridable per project.
- **Retrieval is multilingual for free** - `bge-m3` embeds all languages into one space
  ([decisions section 1](decisions.md)), so a Dutch question can retrieve English chunks. This is
  behaviour, not a setting; there is nothing to toggle.

### 3.3 Defaults
Default **provider** (the model picker's selection), default **tools** (each capped by the
user's `gert_tools` JWT entitlement -
[auth](auth.md#tool-entitlements-allowed-tools-in-the-jwt)), and **memory mode**
([section 2.3](#23-memory)). These seed every new project and conversation unless overridden.
Sampling is not here - it belongs to the selected provider
([section 4](#4-llm-providers--models)), not the user.

---

## 4. LLM providers & models

- **The admin owns the catalog.** The published provider list is server config
  ([tech-stack](tech-stack.md)): a `Gert:Chat:Providers` map keyed by slug, each entry a named
  preset carrying its own connection (an **OpenAI-compatible** base URL + upstream model -
  vLLM today; any compatible endpoint) **and its sampling**
  ([installation section providers](../installation/configuration.md#4-gertchatproviders---the-chat-provider-catalog)).
  `GET /api/models` ([rest-api](rest-api.md)) surfaces the catalog to the picker (the slug
  is the `id`).
- **Users select, they don't add endpoints.** A user/project/conversation chooses a
  `model_id` - a provider slug - from the catalog; it is validated to be **in the
  allowlist** ([principle #6](principles.md)). Users cannot point Gert at an arbitrary URL
  - that would be both a security hole and a way to exfiltrate the conversation off-box.
  (Per-user BYO provider keys are a deliberate non-feature for now - [section 9](#9-open-decisions).)
- **Sampling is per-provider config, not a user/project/conversation knob.** Temperature,
  top_p, the penalties, stop, seed - and the vLLM extensions `top_k` / `min_p` /
  `repetition_penalty` and the template kwargs `chat_template_kwargs.enable_thinking` /
  `...preserve_thinking` - are all set **on the provider** in `appsettings`, not by the user.
  The same physical model can appear under several slugs with different sampling, so
  **picking a thinking vs an instruct provider is how you change the decode** - there is no
  per-request sampling override and no separate thinking toggle. The per-round `max_tokens`
  is the operator's `Gert:Turn:MaxTokensPerRound`
  ([installation section 9](../installation/configuration.md#9-gertturn---the-detached-turn-pipeline)).
- **The embedding model is not configurable and is effectively immutable.** It bakes into every
  `rag.db`'s `vec0` dimension (`FLOAT[1024]` for `bge-m3` - [decisions section 1](decisions.md)).
  Changing it would invalidate every stored vector across every project and force a full
  re-embed, so it is a deployment-wide constant, never a per-user/-project knob.

---

## 5. Data lifecycle (user-facing)

Everything here is a delete on the filesystem - the two-DB-per-project split is what makes the
fine-grained ones possible ([storage-and-data](storage-and-data.md)).

| Action | Effect | Mechanism |
|--------|--------|-----------|
| **Forget documents** (a project) | wipe that project's corpus, keep its chats | clear `rag.db` (+ `files/`); `chat.db` untouched |
| **Clear memory** (a project) | drop curated/auto memory | delete `memory/` + its `kind='memory'` rows |
| **Delete a project** | remove the whole workspace at once | drop the project's `chat.db` + `rag.db` + blobs together |
| **Delete account data** | erase everything the app stores | drop every store the user has - all projects |
| **Export** | take your data with you | per-project or whole-account archive: conversations as JSON/Markdown + original `files/` |

Two honest edges:
- **The `default` project can be emptied but not removed** - deleting it clears its contents and
  leaves an empty `default`, so the user always has a landing project.
- **Account deletion erases data, not identity.** The app drops the user's data across its stores;
  the Pocket ID account is the IdP's to remove ([operations -> user lifecycle](operations.md#user-lifecycle---remove-a-user)).
  Full off-boarding is "delete my data here" **+** "remove me in Pocket ID."

---

## 6. What is *not* configurable

Stating the boundaries as plainly as the knobs:

- **Embedding model / dimension** - deployment-wide, immutable ([section 4](#4-llm-providers--models)).
- **Provider endpoints** - admin-only; users pick from the catalog, never add URLs.
- **The user key & isolation model** - derived from the token; not a setting ([principles](principles.md)).
- **Cross-project search** - does not exist; projects are isolated by design ([section 2](#2-projects)).
- **Bounds** - max upload size, instruction length, param ranges, tool entitlements: admin caps,
  not user-raisable. The `gert_tools` JWT entitlement is the hard ceiling on tools regardless of
  any toggle ([auth](auth.md), [chat-and-tools](chat-and-tools.md)).

---

## 7. API surface

The settings/projects/memory/lifecycle endpoints, in shape form - the full contracts live
in [rest-api](rest-api.md), the SPA surface in [section 8](#8-the-spa-surface).

```
# user settings
GET    /api/settings                       # theme, languages, defaults, memory mode
PUT    /api/settings

# projects
GET    /api/projects                       # list (the user.db project registry)
POST   /api/projects                       # { name, description?, instructions?, defaults? }
GET    /api/projects/{pid}                  # config + counts
PATCH  /api/projects/{pid}                  # rename / instructions / defaults
DELETE /api/projects/{pid}                  # drop the project  (default -> emptied, not removed)

# project memory
GET    /api/projects/{pid}/memory
POST   /api/projects/{pid}/memory          # add/edit an entry (also embeds it)
DELETE /api/projects/{pid}/memory/{id}

# data lifecycle
POST   /api/projects/{pid}/forget-documents
GET    /api/projects/{pid}/export
GET    /api/account/export
DELETE /api/account                        # erase all of this user's data
```

**All data endpoints are project-scoped** - conversations, the message endpoint, documents,
and artifacts live under `/api/projects/{pid}/...` (with `default` a valid `pid`).
There is **no `userId` in any path** (it's the token); the `pid` is safe per
[section 2.5](#25-path-resolution--why-a-request-supplied-project-id-is-still-idor-safe). Example:

```
GET  /api/projects/{pid}/conversations
POST /api/projects/{pid}/conversations/{id}/messages   # 202 - detached turn (rest-api.md)
GET  /api/projects/{pid}/documents
```

Admin is unchanged: it lists/deletes **users** ([rest-api](rest-api.md)); project lifecycle is
the user's own.

---

## 8. The SPA surface

Where this lands in the SPA (`Gert.Api/wwwroot`, [ui-components](ui-components.md)):

- **Project picker** (`components/sidebar/project-picker.js`) - switch/create/delete projects;
  the sidebar's project context above the conversation list.
- **Settings modal** (`components/settings/settings-modal.js`, opened from the user chip) -
  theme, reply language, default provider (the model picker's default).
- **Project settings** - name, instructions, defaults, memory editor, "forget documents".
- **Account** - export, delete-my-data, plus the Pocket-ID off-boarding note.
- **i18n** - *not built yet*: the UI-language setting is reserved ([section 3.2](#32-language)), but
  locale dictionaries and an i18n pass over component strings remain an additive follow-up.

---

## 9. Open decisions

- **Custom accent / theming beyond Manila/Ember** - fixed palettes for v1; revisit if asked.
- **Per-user BYO provider keys** - non-feature for now (security + keeps everything on-box);
  reconsider only if a user genuinely needs an off-box model.
- **Export format** - JSON + original files is the floor; Markdown transcripts are a nice-to-have.
- **Auto-memory** - ship `manual` first; enable `auto` ("remember this") once there's a UI to
  review/undo what the model wrote.
