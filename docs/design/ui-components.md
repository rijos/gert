# UI components & `wwwroot` layout

How the **VanJS SPA** (`src/Gert.Api/wwwroot`) is organised on disk: the folder
structure, the four layers, where state and I/O live, the CSS split, and the
**no-npm** dev/release pipeline. The *authoring conventions* — the `component()`
factory, theming rules, list rendering, formatting — live in the
[SPA style guide](spa-style-guide.md); this doc is the map, that one is the manual.

This expands the `Gert.Api/wwwroot` node from [Tech stack → Solution layout](tech-stack.md#solution-layout-projects).
(The SPA was originally built from a static design mockup, `uistyle.html`, since
removed — the tree below documents the real, shipped app.)

> **One-line architecture:** `wwwroot` **is** the source. Components are plain ES
> modules a browser loads as-is — no bundler, no transpile. In dev you debug the exact
> files you wrote; on publish, .NET minifies them in place. VanJS is vendored locally,
> so the SPA has **zero external runtime dependencies** — fitting for a self-hosted box.

---

## 1. Guiding choices

| Decision | Choice | Why |
|----------|--------|-----|
| Framework | **VanJS** (vendored, ~1 KB) | Already the chosen SPA framework ([tech-stack](tech-stack.md)). Components are just functions returning DOM nodes — no compiler needed. |
| Module system | **Native ES modules** + an **import map** | The browser resolves `import` directly. `wwwroot` is the source; what you debug is what you wrote. |
| Dev build | **None.** `dotnet run` serves raw source | Real files, real line numbers in devtools, instant refresh. No Node, no watcher. |
| Release build | **.NET-only minify on `dotnet publish`** (NUglify) — **no npm** | Minified `.js`/`.css` written into `wwwroot` with paths unchanged, so the ESM import graph still resolves. See [§6](#6-devrelease-pipeline-no-npm). |
| CSS | **Tokens-only theming; component CSS co-located** via the `component()` factory; four global sheets (`tokens` · `base` · `layout` · `primitives`) | A component's rules live with the component (CSP-clean adopted stylesheets); only un-ownable rules stay global — [style guide §2](spa-style-guide.md#2-theming--derive-everything-from-global-tokens). |
| State | **VanJS reactive `state/` stores**, no DOM | Components bind to stores; stores never touch the DOM. One-way: store → view. |
| I/O | **`services/` only** | Components never `fetch`. All `/api` traffic goes through a service. |
| Dependencies | **Vendored in `lib/`** (VanJS, VanX, tiny router) + in-house micro-libs (markdown, highlight, sandbox) | Offline-friendly, version-pinned, no CDN, no package manager. |

---

## 2. Directory layout

`Gert.Api/wwwroot` is the authoring root and what is served — the same
files are what ship (minified) on publish.

```
wwwroot/
  index.html                 # shell: global <link> styles, import map, <script type="module" src="/app.js">
  app.js                     # bootstrap: theme → session (PKCE) → mount AppShell → router → initial loads
  favicon.svg

  lib/                       # vendored + tiny in-house infrastructure — no package manager
    van.js                   #   VanJS core (~1 KB)
    van-x.js                 #   VanX — reactive lists/objects for keyed collections
    router.js                #   minimal History-API router (real paths, :params, data-link interception)
    component.js             #   the component() factory — CSP-clean adopted stylesheets (style guide §1)
    action.js                #   attempt(): run a user action; on failure, toast instead of swallowing
    markdown.js              #   vendored markdown renderer + sanitizer — never raw HTML (security F4)
    highlight.js             #   regex code-fence tokenizer — emits DOM nodes, never HTML strings (F4)
    artifact-sandbox.js      #   builds the srcdoc + per-document CSP for artifact iframes (F3)

  state/                     # reactive stores — van.state / vanX.reactive; NO DOM, NO fetch
    ui.js                    #   theme, nav/panel collapse, drawers, panel width, active artifact tab
    auth.js                  #   displayable identity (user chip, admin flag) — the token is NOT here (F2)
    chat.js                  #   conversations, active thread, message stream, streaming flag, tools, context tokens
    models.js                #   model catalog + current selection
    knowledge.js             #   documents + per-doc ingest status
    artifacts.js             #   artifacts open in the canvas for the active thread

  services/                  # side effects — the ONLY place that talks to /api
    http.js                  #   fetch wrapper: base URL, Bearer header, JSON, error shaping
    auth.js                  #   PKCE login, silent refresh; the access token lives here, in memory only (F2)
    chat.js                  #   POST message (202, detached turn); consume the TurnEvent stream (WS → SSE → poll)
    conversations.js         #   list / open / rename / delete
    projects.js              #   list / create / switch / delete projects
    documents.js             #   upload, poll status, delete
    memory.js                #   project memory entries
    models.js                #   GET /api/models
    settings.js              #   GET/PUT /api/settings
    admin.js                 #   GET/DELETE /api/admin/users (key → user)

  icons/
    icons.js                 # named SVG factories: Icon('search'), ThemeGlyphs, … (one glyph, one place)

  components/
    app-shell.js             # 3-col .app grid + layout state classes + scrim

    ui/                      # design-system primitives — reused everywhere
      button.js  pill.js  badge.js  switch.js  seg-toggle.js
      menu.js  dropdown.js  modal.js  toast.js  progress-bar.js

    sidebar/
      sidebar.js             # column container + responsive drawer; the brand header and
      │                      #   new-chat button are single-use leaves and live in here
      project-picker.js      # switch/create/delete projects (configuration §2)
      convo-list.js          # grouped (Today/Yesterday/…) git-graph branches
      convo-item.js          # one .convo row with its .node
      user-chip.js           # avatar + name + auth line + settings button

    main/
      top-bar.js             # collapse btn · title · theme · model picker · panel toggle
      conv-title.js          # editable conversation title
      tools-menu.js          # composer dropdown of tool toggles (RAG/Search/Sandbox/Todos/Clock
      │                      #   + Canvas — one switch for the make/edit/read artifact trio) + "Use my docs"
      theme-toggle.js        # sun/moon — glyph swap driven by tokens, not JS theme checks
      model-picker.js        # dropdown menu + model items + capability badges
      message-stream.js      # the scrolling thread
      message.js             # user/bot bubble — citations, footnotes, streaming caret, thinking block
      tool-card.js           # tool-call card: expandable, status, doc-hits, stdout, todo checklist, errors
      composer.js            # textarea (autogrow) + attachments + tools + send + hint
      context-ring.js        # context-window usage circle in the composer

    settings/
      settings-modal.js      # user settings popup (theme, reply language, default model) — opened from the user chip
      model-settings-modal.js# per-model generation-param overrides

    canvas/
      canvas-panel.js        # right pane container + drawer behaviour
      canvas-bar.js          # artifact tab strip + bar tools (KB toggle, expand, close)
      artifact-tabs.js       # the tab list
      artifact.js            # polymorphic dispatcher: picks a viewer by artifact kind
      artifacts/
        markdown-artifact.js #   sanitized render (lib/markdown.js) + source view
        html-artifact.js     #   sandboxed <iframe srcdoc> (lib/artifact-sandbox.js) + source
        svg-artifact.js      #   sandboxed iframe — SVG can carry script (F3) + source
        code-artifact.js     #   highlighted lines (lib/highlight.js)
      knowledge-panel.js     # kb-view: header + privacy note + use-in-chat switch
      drop-zone.js
      doc-list.js
      doc-row.js             # file icon + meta + status pill (ready/processing/failed) + trash

  pages/                     # routed views (the router swaps these into the main region)
    chat.js                  # default — the conversation screen (/, /c/:id)
    admin/
      users.js               # /admin/users — admin-only user list

  styles/                    # the global cascade — only rules no single component owns (style guide §2)
    tokens.css               # design tokens — light-dark(Manila, Ember), [data-theme] scheme pins; loads first
    base.css                 # reset, body + paper grain, scrollbars, keyframes
    layout.css               # .app grid, collapse states, responsive drawers + ALL @media
    primitives.css           # shared bare-class utilities (.btn, .ghost, .trash, .field …)
```

> The app is mostly one screen (`chat`); `admin/users` is the only other true page.
> **Settings is a modal**, not a route — `components/settings/settings-modal.js`,
> opened from the user chip. Deep links work because the server routes unknown
> paths to `index.html` via `MapFallbackToFile` ([tech-stack](tech-stack.md)).

---

## 3. The four layers

Dependencies point **one way**: `components` → `state` + `services` → `lib`. The same
inward-only discipline the .NET side enforces ([principles](principles.md)), applied to
the front end.

```
  pages/        compose components into a routed screen
     │
     ▼
  components/   pure: props + state in → DOM node out. Never fetch.
     │   reads ▼            ▲ binds
  state/  ──────┘           │   reactive stores (van.state). No DOM, no I/O.
     ▲ updated by           │
  services/  ───────────────┘   the only code that calls /api. Updates state.
     │
     ▼
  lib/          vendored framework + infrastructure. Depends on nothing.
```

- **A component never calls `fetch`.** It reads a store and renders; user actions call a
  *service* (usually through `lib/action.js`'s `attempt()`, which toasts on failure), the
  service updates the *store*, and the binding re-renders. This keeps "one source of
  truth" honest and makes every screen testable by seeding a store.
- **A store never touches the DOM.** It holds `van.state(...)` / `vanX.reactive(...)`
  values and the actions that mutate them.
- **A service never imports a component.** It speaks HTTP and returns/derives data.

---

## 4. Component conventions

The authoring rules live in the [SPA style guide](spa-style-guide.md) — the
`component({ name, css, view })` factory, kebab-case filenames with PascalCase
exports, token rules, no local `@media`, list rendering, formatting. The layout-level
rules that belong here:

1. **One component per file**, in the `components/<area>/` that owns it. Co-locate a
   trivial subcomponent; promote it to its own file once reused or past ~40 lines
   (e.g. the brand header lives inside `sidebar.js`).
2. **Named exports only** — no `default`. Keeps imports greppable and the import map flat.
3. **No top-level side effects** (no `fetch`, no global mutation at import time) —
   a component module must be importable by the test harness without booting the app.
4. **I/O through `services/`, state through `state/`.** A click handler calls a service;
   it does not build a `Request`.

---

## 5. Cross-cutting concerns

### Styling — tokens global, component CSS co-located
The global cascade is only what no single component owns
([style guide §2](spa-style-guide.md#2-theming--derive-everything-from-global-tokens)):

| File | Holds |
|------|-------|
| `tokens.css` | every design token, defined once with `light-dark(manila, ember)`; the `[data-theme]` scheme pins; **must load first** |
| `base.css` | reset, `body` + paper grain, scrollbars, `@keyframes` |
| `layout.css` | the `.app` grid, collapse/wide states, responsive drawers, scrim — and **all `@media`** |
| `primitives.css` | shared bare-class utilities (`.btn`, `.ghost`, `.trash`, `.field` …) applied by class string across many components |

Everything else ships **inside the component** via the `component()` factory, which
adopts the CSS as a Constructable Stylesheet — CSP-clean under `style-src 'self'`
(no `unsafe-inline`), and cascading after the globals so `layout.css` keeps priority.

### Theme
Two themes — **Manila** (paper light) and **Ember** (refined dark). Color tokens are
defined once with `light-dark()`; the default `:root` follows the OS via
`color-scheme: light dark`, and the explicit `[data-theme="manila"|"ember"]` scopes
just pin the scheme. `state/ui.js` owns the toggle: it sets
`documentElement[data-theme]` and persists to `localStorage` as a first-paint cache —
the server-side setting is the cross-device truth
([configuration §3.1](configuration.md#31-theme)).

### Icons
`icons/icons.js` exposes every SVG glyph once as a named factory —
`Icon("trash", { size: 14 })` — so a glyph is defined in exactly one place.

### State stores
Scalar UI state uses VanJS `van.state` / `van.derive`. Keyed collections that churn —
the conversation list, the doc list, the message stream — hold **VanX `reactive`** row
objects, so a streamed token or a single doc's status change re-renders just that node;
list *membership* changes re-render via a map-rebuild binding (`vanX.list` keyed
rendering is the documented opt-in for hot lists, not yet adopted —
[style guide §4](spa-style-guide.md#4-lists--reactive-rows-rebuild-on-membership)).

### Routing
`lib/router.js` is a tiny History-API router. The full route table (declared once, in
`app.js`): `/` and `/c/:id` → `pages/chat.js`, `/admin/users` → `pages/admin/users.js`.
Settings opens as a modal from the user chip — deliberately not a route.

### Streaming
`services/chat.js` POSTs the message (a **202 detached turn** —
[rest-api](rest-api.md#sending-a-message-detached-turn)), then consumes the
conversation's TurnEvent stream over the best available transport — **WebSocket, then
SSE, then range polling**, all gap-free over the same `seq` cursor — and pushes each
event onto `state/chat.js` (+ `state/artifacts.js`). `message.js`, `tool-card.js`, and
the canvas bind to that state, so the typewriter effect, tool-card progress, and
artifact tabs are just reactive renders of incoming events.

### Security: token handling & rendering
The SPA holds a bearer token **and** renders untrusted model output, so the two are kept apart by
construction (full rationale in [security](security.md#3-findings--remediations)):

- **Token in memory only.** `services/auth.js` keeps the access token in a module variable, **not
  `localStorage`** — so an injected script has nothing persistent to read ([security F2](security.md#3-findings--remediations)).
  `state/auth.js` holds only displayable identity claims. If a refresh token must survive a reload,
  it lives in an `httpOnly; Secure; SameSite=Strict` cookie, never JS-readable storage. (The
  Content-Security-Policy in [operations](operations.md#http-security-headers--csp) is the outer
  wall; this is defence-in-depth behind it.)
- **Artifacts render in a sandboxed iframe.** Both `html-artifact.js` **and** `svg-artifact.js` use
  `<iframe srcdoc sandbox="allow-scripts">` — crucially **without `allow-same-origin`** (the two
  together would defeat the sandbox) — with a restrictive per-document CSP built by
  `lib/artifact-sandbox.js`. SVG gets the same treatment as HTML because inline `<svg>` can carry
  `<script>`/`onload` that would otherwise run in the app origin and steal the token
  ([security F3](security.md#3-findings--remediations)). The **Source** view shows raw text, never a
  live injected node.
- **Markdown is sanitized by construction.** `lib/markdown.js` (and `lib/highlight.js` for code)
  build **real DOM nodes from `textContent`** — raw HTML is never interpreted, `javascript:`/`data:`
  URLs are stripped, and external links get `rel="noopener noreferrer" target="_blank"`. VanJS text
  bindings escape by default; these two vendored micro-libs are the only "render rich text" paths
  ([security F4](security.md#3-findings--remediations)).

---

## 6. Dev/release pipeline (no npm)

The whole point of native ESM: **the dev build is "no build."** Minification is a
publish-time concern handled entirely by .NET.

### Development — raw source
- `Gert.Api` serves `wwwroot/` directly in `Development` (`UseStaticFiles`,
  no-cache, `MapFallbackToFile("index.html")`).
- The browser loads `app.js` as a module and resolves every `import` by path. The bare
  specifiers resolve through the **import map** in `index.html`:

```html
<script type="importmap">
{ "imports": {
    "van":        "/lib/van.js",
    "vanjs-core": "/lib/van.js",
    "van-x":      "/lib/van-x.js"
} }
</script>
<script type="module" src="/app.js"></script>
```

- What you see in devtools **is** the file on disk — real names, real line numbers, no
  source maps. Edit, refresh, done.

### Release — minify in place, on `dotnet publish`
An MSBuild target runs **[NUglify](https://github.com/trullock/NUglify)** (pure .NET,
NuGet, **no npm**) over the assets as they land in the publish output, minifying each
`.js`/`.css` **to the same relative path** (`tools/Gert.Web.Minify`, invoked from
`Gert.Api.csproj` after publish). Because paths don't change, the ESM import graph and
the import map keep resolving — we minify, we don't bundle or rename.

**ESM caveat — validated.** NUglify must parse modern module syntax; this was verified
against the real `wwwroot` on `dotnet publish` (build unit U14 — the published graph
still resolves). The minify-in-place, **no-bundle** design remains the safety net: a
file that ever trips the parser stays raw (or whitespace-minified) without breaking
the import graph — a single file failing never cascades.

**Cache-busting:** keep filenames stable (so imports don't need rewriting) and bust via
HTTP — ETags plus versioned query strings on the `index.html` entry tags. Content-hashed
filenames are deliberately avoided here because they'd force rewriting every relative
`import`.

**If we ever want bundling/fingerprinting** without npm: `LigerShark.WebOptimizer.Core`
(also pure .NET / NUglify, runtime pipeline) is the drop-in upgrade — deferred, since the
no-build ESM model is simpler and HTTP/2 multiplexing makes many small modules cheap.

---

## 7. Feature → component map

Every interactive piece of the app, and where it lives.

| Feature | File(s) |
|----------------|---------|
| 3-column grid, collapse/wide states, mobile drawers, scrim | `components/app-shell.js`, `styles/layout.css` |
| Brand header + new chat | inside `components/sidebar/sidebar.js` (single-use leaves) |
| Project picker (switch/create/delete) | `components/sidebar/project-picker.js` |
| Grouped git-graph conversation list | `components/sidebar/convo-list.js` + `convo-item.js` |
| User chip (avatar, auth line, settings) | `components/sidebar/user-chip.js` |
| Top bar shell | `components/main/top-bar.js` |
| Editable conversation title | `components/main/conv-title.js` |
| Tool toggles (RAG / Search / Sandbox / Todos / Clock + the Canvas trio as one switch) | `components/main/tools-menu.js` (composer dropdown) |
| Theme toggle | `components/main/theme-toggle.js` + `state/ui.js` |
| Model picker dropdown + capability badges | `components/main/model-picker.js`, `components/ui/dropdown.js`, `badge.js` |
| Per-model generation params | `components/settings/model-settings-modal.js` |
| User settings (theme, reply language, default model) | `components/settings/settings-modal.js` |
| Message thread + user/bot bubbles | `components/main/message-stream.js` + `message.js` |
| Citations, footnotes, streaming caret, thinking block | inside `components/main/message.js` |
| Tool-call cards (expandable, status, doc-hits, stdout, todos, errors) | `components/main/tool-card.js` |
| Composer (autogrow, attachments, tools, send) | `components/main/composer.js` |
| Context-window usage ring | `components/main/context-ring.js` |
| Canvas tab strip + bar tools | `components/canvas/canvas-bar.js` + `artifact-tabs.js` |
| Markdown / HTML / SVG / Code artifacts | `components/canvas/artifacts/*.js` via `artifact.js` |
| Knowledge panel (privacy, use-in-chat switch) | `components/canvas/knowledge-panel.js`, `components/ui/switch.js` |
| Drop zone + doc list + status pills + trash | `components/canvas/drop-zone.js`, `doc-list.js`, `doc-row.js`, `components/ui/pill.js` |
| Toasts / modals / progress | `components/ui/toast.js`, `modal.js`, `progress-bar.js` |
| Admin user list | `pages/admin/users.js`, `services/admin.js` |

---

## 8. Decisions & open choices

- **VanX for reactive collections — decided, shipped.** `van-x` is vendored in `lib/`
  and every keyed collection's rows — the conversation list, the doc list, the message
  stream — are `vanX.reactive`. The ~1 KB buys per-field updates within a row;
  membership changes currently re-render via map-rebuild bindings, with `vanX.list`
  per-item keyed rendering the opt-in upgrade
  ([style guide §4](spa-style-guide.md#4-lists--reactive-rows-rebuild-on-membership)).
  Plain `van.state` is the tool for scalar UI state in `state/ui.js` (theme, layout flags).
- **Minifier validation — resolved.** NUglify minifies the real ESM source cleanly;
  verified on `dotnet publish` (U14). The raw-fallback safety net stays in place.
- **i18n / UI language — not built.** The settings design reserves a UI-language
  setting ([configuration §3.2](configuration.md#32-language)); locale dictionaries
  and an i18n pass on component strings would be the additive follow-up.
