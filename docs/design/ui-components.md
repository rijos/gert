# UI components & `wwwroot` layout

How the **VanJS SPA** (`Gert.Web/` → served from `Gert.Api/wwwroot`) is organised on
disk: the folder structure, the component conventions, where state and I/O live, the
CSS split, and the **no-npm** dev/release pipeline.

This expands the `Gert.Web/` node from [Tech stack → Solution layout](tech-stack.md#solution-layout-projects).
It derives directly from the interface in [`uistyle.html`](../../uistyle.html) — every
moving part in that mockup maps to a file here (see [Feature → component map](#feature--component-map)).

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
| CSS | **Global cascade, split by concern** (`tokens` · `base` · `layout` · `components`) | Keeps the mockup's flat class names and token system; one cascade, no scoping machinery. |
| State | **VanJS reactive `state/` stores**, no DOM | Components bind to stores; stores never touch the DOM. One-way: store → view. |
| I/O | **`services/` only** | Components never `fetch`. All `/api` traffic goes through a service. |
| Dependencies | **Vendored in `lib/`** (VanJS, `van-x`, tiny router) | Offline-friendly, version-pinned, no CDN, no package manager. |

---

## 2. Directory layout

`Gert.Web/` is the authoring root and mirrors the served `wwwroot` **1:1** — the same
files are what ship (minified) on publish.

```
wwwroot/
  index.html                 # shell HTML: import map + <script type="module" src="app.js">
  app.js                     # bootstrap: init stores, auth, router; mount <AppShell>

  lib/                       # vendored, version-pinned — no package manager
    van.js                   #   VanJS core (~1 KB)
    van-x.js                 #   reactive lists/objects for keyed collections (convos, docs, messages)
    router.js                #   minimal History-API router (~40 lines)

  state/                     # reactive stores — van.state(); NO DOM, NO fetch
    ui.js                    #   theme, nav/panel collapse, panel-wide, mobile drawers, active artifact tab
    auth.js                  #   access token (in-memory only — not localStorage), user identity
    chat.js                  #   conversations, active conversation, message stream, streaming flag
    models.js                #   available models + current selection
    knowledge.js             #   documents + per-doc ingest status
    artifacts.js             #   artifacts open in the canvas for the active thread

  services/                  # side effects — the ONLY place that talks to /api
    http.js                  #   fetch wrapper: base URL, Bearer header, JSON, error shaping
    auth.js                  #   PKCE login, token store, silent refresh (Pocket ID)
    conversations.js         #   list / create / rename / delete  → /api/conversations
    chat.js                  #   POST message; consume the SSE ChatEvent stream
    documents.js             #   upload, poll status, delete       → /api/documents
    models.js                #   GET /api/models
    admin.js                 #   GET /api/admin/users (key → user)

  icons/
    icons.js                 # named SVG factories: Icon('search'), Icon('trash') … (de-dupes the mockup's inline SVGs)

  components/
    app-shell.js             # 3-col .app grid + layout state classes + scrim + (dev) legend

    ui/                      # design-system primitives — reused everywhere
      button.js  ghost-button.js  pill.js  badge.js  switch.js
      seg-toggle.js  menu.js  modal.js  loader.js  toast.js

    sidebar/
      sidebar.js             # column container + responsive drawer behaviour
      brand.js               # mark + title + version + drawer-close
      new-chat.js
      convo-list.js          # grouped (Today/Yesterday/…) git-graph branches
      convo-item.js          # one .convo with its .node
      user-chip.js           # avatar + name + auth line + settings button

    main/
      top-bar.js             # collapse btn · title · tool chips · theme · model picker · panel toggle
      conv-title.js          # editable conversation title
      tool-chips.js          # RAG / Search / Sandbox on-off chips
      theme-toggle.js
      model-picker.js        # dropdown menu + model items + capability badges
      message-stream.js      # the scrolling thread
      message.js             # user / bot message (role header + body)
      tool-card.js           # toolzone node card: expandable, done state, doc-hits, code, stdout
      citation.js            # inline [n] superscript
      footnotes.js           # footnote list under a bot message
      caret.js               # streaming typewriter caret
      composer.js            # textarea (autogrow) + attach/use-docs toggles + send + hint

    canvas/
      canvas-panel.js        # right pane container + drawer behaviour
      canvas-bar.js          # artifact tab strip + bar tools (KB toggle, expand, close)
      artifact-tabs.js       # the tab list
      artifact.js            # polymorphic dispatcher: picks a viewer by artifact.type
      artifact-head.js       # type badge + name + Rendered/Source seg toggle (shared)
      artifacts/
        markdown-artifact.js #   sanitized render (no raw HTML) + source
        html-artifact.js     #   sandboxed <iframe srcdoc> + source
        svg-artifact.js      #   sandboxed iframe (SVG can carry script) + source
        code-artifact.js     #   linted lines + Problems panel
      knowledge-panel.js     # kb-view: header + privacy + use-in-chat switch
      drop-zone.js
      doc-list.js
      doc-row.js             # file icon + meta + status pill (ready/proc/fail) + trash

  pages/                     # routed views (the router swaps these into the main region)
    chat.js                  # default — the conversation screen (/, /c/:id)
    settings.js              # /settings — user settings
    admin/
      users.js               # /admin/users — admin-only user list

  styles/
    tokens.css               # :root design tokens + [data-theme=dark] + prefers-color-scheme
    base.css                 # reset, body + paper grain, scrollbars, keyframes
    layout.css               # .app grid, collapse states, responsive drawers, scrim
    components.css           # component classes — @imports the area partials below
    areas/
      sidebar.css  main.css  canvas.css  composer.css  artifacts.css
```

> `pages/` (plural) replaces the `page/user/…` from your sketch — conventional, and the
> server already routes deep links here via `MapFallbackToFile("index.html")`
> ([tech-stack](tech-stack.md)). The app is mostly one screen (`chat`); `settings` and
> `admin/users` are the only true page swaps.

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
  lib/          vendored framework + router. Depends on nothing.
```

- **A component never calls `fetch`.** It reads a store and renders; user actions call a
  *service*, the service updates the *store*, and the binding re-renders. This keeps the
  Console-style "one source of truth" honest and makes every screen testable by seeding a
  store.
- **A store never touches the DOM.** It holds `van.state(...)` values and the actions that
  mutate them.
- **A service never imports a component.** It speaks HTTP and returns/derives data.

---

## 4. Component conventions

1. **One component per file.** File name `kebab-case.js`; exported factory `PascalCase`.
   Co-locate a trivial subcomponent; promote it to its own file once reused or past ~40 lines.
2. **Named exports only** — no `default`. Keeps imports greppable and the import map flat.
3. **Factory shape:** `export const Foo = (props = {}) => node`. It returns a VanJS DOM
   node and has **no top-level side effects** (no `fetch`, no global mutation at import time).
4. **Reactivity via functions**, the VanJS way — pass a function for any bound attr/child:
   `class: () => state.open.val ? "menu open" : "menu"`.
5. **Styling via the existing flat class names** defined in `styles/`. Inline `style` only
   for genuinely dynamic values (e.g. a computed width); never for static rules.
6. **I/O through `services/`, state through `state/`.** A click handler calls a service; it
   does not build a `Request`.

Representative component — `components/sidebar/convo-item.js`:

```js
import van from "../../lib/van.js";
import * as chat from "../../state/chat.js";
import * as svc from "../../services/conversations.js";

const { div, span } = van.tags;

export const ConvoItem = (convo) =>
  div(
    {
      class: () => "convo" + (chat.activeId.val === convo.id ? " active" : ""),
      onclick: () => svc.open(convo.id),   // service updates the store; binding re-renders
    },
    span({ class: "node" }),
    span({ class: "t" }, convo.title),
  );
```

Bootstrap — `app.js`:

```js
import van from "./lib/van.js";
import { mountRouter } from "./lib/router.js";
import { AppShell } from "./components/app-shell.js";
import * as auth from "./services/auth.js";
import * as ui from "./state/ui.js";

ui.restoreTheme();                 // apply saved/OS theme before first paint
await auth.ensureSession();         // PKCE / silent refresh
van.add(document.body, AppShell()); // mount the shell once
mountRouter();                      // swap pages/* into the main region
```

---

## 5. Cross-cutting concerns

### Styling — global, token-driven, split by concern
The mockup's single `<style>` block splits into four global files, loaded in order:

| File | Holds | From the mockup |
|------|-------|-----------------|
| `tokens.css` | `:root` custom properties, `[data-theme=dark]`, `prefers-color-scheme` | the whole `:root{…}` / dark-override block |
| `base.css` | reset, `body` + paper grain, scrollbars, `@keyframes` | `*{}`, `body`, `::-webkit-scrollbar`, `rise`/`blink`/`pulse` |
| `layout.css` | `.app` grid, collapse/wide states, responsive drawers, `.scrim` | `.app…`, the `@media` drawer rules |
| `components.css` | every component's classes; `@import`s `areas/*.css` | sidebar, topbar, stream, composer, canvas, etc. |

`tokens.css` **must load first** so variables exist before any rule references them. Class
names stay exactly as the mockup (`.convo`, `.tcard`, `.composer`, …) — components emit
those classes verbatim, so the stylesheet and the components evolve together.

### Theme
`state/ui.js` owns it: toggling sets `documentElement[data-theme]` and persists to
`localStorage`; when unset, `tokens.css` falls back to `prefers-color-scheme` — identical
to the mockup's `toggleTheme()` logic, just centralised.

### Icons
The mockup repeats the same inline SVGs (file, trash, search, …) many times. `icons/icons.js`
exposes them once as named factories — `Icon("trash", { size: 14 })` — so a glyph is defined
in exactly one place.

### State stores
Scalar UI state uses VanJS `van.state` / `van.derive`. Keyed collections that churn — the
conversation list, the doc list, the message stream — use **`van-x`** (`reactive`, `list`)
from `lib/`, so a streamed message or a single doc's status change re-renders just that row
instead of the whole list. (Decided — see [§8](#8-decisions--open-choices).)

### Routing
`lib/router.js` is a ~40-line History-API router. Routes: `/` and `/c/:id` → `pages/chat.js`,
`/settings` → `pages/settings.js`, `/admin/users` → `pages/admin/users.js`. Deep links work
because the server falls back to `index.html` ([tech-stack](tech-stack.md)); the client
router then renders the matching page into the main region.

### Streaming
`services/chat.js` consumes the SSE `ChatEvent` stream and pushes onto `state/chat.js`;
`message.js`, `tool-card.js`, and `caret.js` bind to it, so the typewriter effect and
tool-card progress are just reactive renders of incoming events — no imperative DOM poking
like the mockup's `replay()`.

### Security: token handling & rendering
The SPA holds a bearer token **and** renders untrusted model output, so the two are kept apart by
construction (full rationale in [security](security.md#3-findings--remediations)):

- **Token in memory only.** `services/auth.js` keeps the access token in a module variable, **not
  `localStorage`** — so an injected script has nothing persistent to read ([security F2](security.md#3-findings--remediations)).
  If a refresh token must survive a reload, it lives in an `httpOnly; Secure; SameSite=Strict`
  cookie, never JS-readable storage. (The Content-Security-Policy in
  [operations](operations.md#http-security-headers--csp) is the outer wall; this is defence-in-depth
  behind it.)
- **Artifacts render in a sandboxed iframe.** Both `html-artifact.js` **and** `svg-artifact.js` use
  `<iframe srcdoc sandbox="allow-scripts">` — crucially **without `allow-same-origin`** (the two
  together would defeat the sandbox), plus a restrictive `csp` on the frame. SVG gets the same
  treatment as HTML because inline `<svg>` can carry `<script>`/`onload` that would otherwise run in
  the app origin and steal the token ([security F3](security.md#3-findings--remediations)). The
  **Source** view shows raw text, never a live injected node.
- **Markdown is sanitized.** `markdown-artifact.js` and bot-message rendering run the renderer with
  **raw HTML disabled** (or output passed through an allow-list sanitizer), strip
  `javascript:`/`data:` URLs, and force external links to `rel="noopener noreferrer" target="_blank"`.
  VanJS text bindings escape by default; any "render HTML" path is the exception that needs this
  ([security F4](security.md#3-findings--remediations)).

---

## 6. Dev/release pipeline (no npm)

The whole point of native ESM: **the dev build is "no build."** Minification is a
publish-time concern handled entirely by .NET.

### Development — raw source
- `Gert.Api` serves `Gert.Web/` as its web root in `Development` (`UseStaticFiles`,
  no-cache, `MapFallbackToFile("index.html")`).
- The browser loads `app.js` as a module and resolves every `import` by path. The bare
  specifier `van` resolves through the **import map** in `index.html`:

```html
<script type="importmap">
{ "imports": {
    "van":   "/lib/van.js",
    "van-x": "/lib/van-x.js"
} }
</script>
<script type="module" src="/app.js"></script>
```

- What you see in devtools **is** the file on disk — real names, real line numbers, no
  source maps. Edit, refresh, done.

### Release — minify in place, on `dotnet publish`
An MSBuild target runs **[NUglify](https://github.com/trullock/NUglify)** (pure .NET,
NuGet, **no npm**) over the assets as they land in `Gert.Api/wwwroot`, minifying each
`.js`/`.css` **to the same relative path**. Because paths don't change, the ESM import
graph and the import map keep resolving — we minify, we don't bundle or rename.

A representative target (a tiny NUglify-based console invoked from publish):

```xml
<!-- Gert.Api.csproj -->
<Target Name="MinifyWebAssets" AfterTargets="Publish" Condition="'$(Configuration)'=='Release'">
  <Exec Command="dotnet run --project ../tools/Gert.Web.Minify -- &quot;$(PublishDir)wwwroot&quot;" />
</Target>
```

…where `tools/Gert.Web.Minify` walks the folder and, per file, calls
`Uglify.Js(src)` / `Uglify.Css(src)` and overwrites it.

**ESM caveat (call out, don't gloss):** NUglify must parse modern module syntax
(`import`/`export`, arrow functions, `const`, template literals, optional chaining).
Recent NUglify handles ES2015+, but validate it against our actual code. The
minify-in-place, **no-bundle** design is the safety net: if one file trips the parser, it
can stay raw (or whitespace-only minified) without breaking the import graph — a single
file failing never cascades.

**Cache-busting:** keep filenames stable (so imports don't need rewriting) and bust via
HTTP — ETags plus `asp-append-version` on the `index.html` entry tags. Content-hashed
filenames are deliberately avoided here because they'd force rewriting every relative
`import`.

**If we ever want bundling/fingerprinting** without npm: `LigerShark.WebOptimizer.Core`
(also pure .NET / NUglify, runtime pipeline) is the drop-in upgrade — deferred, since the
no-build ESM model is simpler and HTTP/2 multiplexing makes many small modules cheap.

---

## 7. Feature → component map

Every interactive piece of [`uistyle.html`](../../uistyle.html), and where it lives.

| Mockup feature | File(s) |
|----------------|---------|
| 3-column grid, collapse/wide states, mobile drawers, scrim | `components/app-shell.js`, `styles/layout.css` |
| Brand mark + version + drawer close | `components/sidebar/brand.js` |
| New chat | `components/sidebar/new-chat.js` |
| Grouped git-graph conversation list | `components/sidebar/convo-list.js` + `convo-item.js` |
| User chip (avatar, auth line, settings) | `components/sidebar/user-chip.js` |
| Top bar shell | `components/main/top-bar.js` |
| Editable conversation title | `components/main/conv-title.js` |
| RAG / Search / Sandbox tool chips | `components/main/tool-chips.js` |
| Theme toggle | `components/main/theme-toggle.js` + `state/ui.js` |
| Model picker dropdown + capability badges | `components/main/model-picker.js`, `components/ui/menu.js`, `badge.js` |
| Message thread + user/bot bubbles | `components/main/message-stream.js` + `message.js` |
| Tool-call cards (expandable, done, doc-hits, code, stdout) | `components/main/tool-card.js` |
| Inline citations + footnotes | `components/main/citation.js` + `footnotes.js` |
| Streaming typewriter caret | `components/main/caret.js` + `services/chat.js` |
| Composer (autogrow, attach, use-docs, send) | `components/main/composer.js` |
| Canvas tab strip + bar tools | `components/canvas/canvas-bar.js` + `artifact-tabs.js` |
| Markdown / HTML / SVG / Code artifacts | `components/canvas/artifacts/*.js` via `artifact.js` |
| Rendered/Source toggle | `components/canvas/artifact-head.js`, `components/ui/seg-toggle.js` |
| Knowledge panel (privacy, use-in-chat switch) | `components/canvas/knowledge-panel.js`, `components/ui/switch.js` |
| Drop zone + doc list + status pills + trash | `components/canvas/drop-zone.js`, `doc-list.js`, `doc-row.js`, `components/ui/pill.js` |
| Toasts / modals / loaders | `components/ui/toast.js`, `modal.js`, `loader.js` |

---

## 8. Decisions & open choices

- **`van-x` for reactive collections — decided.** Vendor `van-x` in `lib/` and use its
  `reactive`/`list` for every keyed collection: the conversation list, the doc list, and
  the message stream. The ~1 KB buys per-item updates, so appending a streamed message or
  flipping one doc's status pill re-renders that row alone, not the whole list. Plain
  `van.state` stays the tool for scalar UI state in `state/ui.js` (theme, layout flags).
- **Minifier validation** — confirm NUglify cleanly minifies our ESM syntax in a spike
  before relying on it; the no-bundle design means a failure is contained, but we should
  know which files (if any) need the raw fallback.
