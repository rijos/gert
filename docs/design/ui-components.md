# UI components & `wwwroot` layout

How the **VanJS SPA** (`src/Gert.Api/wwwroot`) is organised on disk: the folder
structure, the four layers, where state and I/O live, the CSS split, and the
**no-npm** dev/release pipeline. The *authoring conventions* - the `component()`
factory, theming rules, list rendering, formatting - live in the
[SPA style guide](spa-style-guide.md); this doc is the map, that one is the manual.

This expands the `Gert.Api/wwwroot` node from [Tech stack -> Solution layout](tech-stack.md#solution-layout-projects).
(The SPA was originally built from a static design mockup, `uistyle.html`, since
removed - the tree below documents the real, shipped app.)

> **One-line architecture:** `wwwroot` **is** the source. Components are plain ES
> modules a browser loads as-is - no bundler, no transpile. In dev you debug the exact
> files you wrote; on publish, .NET minifies them in place. VanJS is vendored locally,
> so the SPA has **zero external runtime dependencies** - fitting for a self-hosted box.

---

## 1. Guiding choices

| Decision | Choice | Why |
|----------|--------|-----|
| Framework | **VanJS** (vendored, ~1 KB) | Already the chosen SPA framework ([tech-stack](tech-stack.md)). Components are just functions returning DOM nodes - no compiler needed. |
| Module system | **Native ES modules**, absolute same-origin paths | Every `import` is an absolute path (e.g. `/lib/van.js`) - no bare specifiers, so **no import map** and nothing inline for the CSP to hash. The browser resolves `import` directly; `wwwroot` is the source; what you debug is what you wrote. |
| Dev build | **None.** `dotnet run` serves raw source | Real files, real line numbers in devtools, instant refresh. No Node, no watcher. |
| Release build | **.NET-only minify on `dotnet publish`** (NUglify) - **no npm** | Minified `.js`/`.css` written into `wwwroot` with paths unchanged, so the ESM import graph still resolves. See [section 6](#6-devrelease-pipeline-no-npm). |
| CSS | **Tokens-only theming; component CSS co-located** via the `component()` factory; four global sheets (`tokens` - `base` - `layout` - `primitives`) | A component's rules live with the component (CSP-clean adopted stylesheets); only un-ownable rules stay global - [style guide section 2](spa-style-guide.md#2-theming---derive-everything-from-global-tokens). |
| State | **VanJS reactive `state/` stores**, no DOM | Components bind to stores; stores never touch the DOM. One-way: store -> view. |
| I/O | **`services/` only** | Components never `fetch`. All `/api` traffic goes through a service. |
| Dependencies | **Vendored in `lib/`** (VanJS, VanX, tiny router) + in-house micro-libs (markdown, highlight, sandbox, math) | Offline-friendly, version-pinned, no CDN, no package manager. **No third-party code touches model output** - markdown and math are both our own. |

---

## 2. Directory layout

`Gert.Api/wwwroot` is the authoring root and what is served - the same
files are what ship (minified) on publish.

```
wwwroot/
  index.html                 # shell: global <link> styles, <script type="module" src="/app.js">
  app.js                     # bootstrap: theme -> session (PKCE) -> mount AppShell -> router -> initial loads
  favicon.svg

  lib/                       # vendored + tiny in-house infrastructure - no package manager
    van.js                   #   VanJS core (~1 KB)
    van-x.js                 #   VanX - reactive lists/objects for keyed collections
    router.js                #   minimal History-API router (real paths, :params, data-link interception)
    component.js             #   the component() factory - CSP-clean adopted stylesheets (style guide section 1)
    action.js                #   attempt(): run a user action; on failure, toast instead of swallowing
    markdown.js              #   THIN facade: parse -> render -> assignHeadingIds; re-exports renderMarkdown / sanitizeUrl / NODE_TYPES (security F4)
    render/                  #   the in-house markdown renderer, split by concern (markdown.js wires + re-exports them)
      url.js                 #     sanitizeUrl / sanitizeImgUrl / isExternal / slugify - the single URL/slug safety source (F4)
      lines.js               #     the LINE_KINDS classifier + bounded block parser -> markdown AST (math/code are opaque leaves)
      inline.js              #     the O(n) inline scanner (tokenizeInline / links / emphasis / entities / autolinks)
      dom.js                 #     the structural renderer: AST -> DOM via ONE guarded createEl(ns,tag,attrs) per-(ns,tag) allow-list; calls MdMath/MdCode (F4)
    markdown-links.js        #   attachLinkConfirm(host): delegated click -> Modal confirm before an external link leaves the app (imports isExternal from render/url.js)
    highlight.js             #   regex code-fence tokenizer - emits DOM nodes, never HTML strings (F4); wrapped by the MdCode leaf
    smath.js                 #   in-house TeX -> native <math> MathML; wrapped by the MdMath leaf
    artifact-sandbox.js      #   builds the srcdoc + per-document CSP for artifact iframes (F3)
    i18n.js                  #   t() UI translation - English source text as key, nl dictionary;
                             #   resolves once per load (localStorage -> navigator.language -> en),
                             #   settings persists ui_language and reloads on switch

  state/                     # reactive stores - van.state / vanX.reactive; NO DOM, NO fetch
    ui.js                    #   theme, nav/panel collapse, drawers, panel width, active artifact tab
    auth.js                  #   displayable identity (user chip, admin flag) - the token is NOT here (F2)
    chat.js                  #   conversations, active thread, message stream, streaming flag, tools, context tokens
    models.js                #   model catalog + current selection
    knowledge.js             #   documents + per-doc ingest status
    artifacts.js             #   artifacts open in the canvas for the active thread

  services/                  # side effects - the ONLY place that talks to /api
    http.js                  #   fetch wrapper: base URL, Bearer header, JSON, error shaping
    auth.js                  #   PKCE login, silent refresh; the access token lives here, in memory only (F2)
    chat.js                  #   POST message (202, detached turn); consume the TurnEvent stream (SSE -> poll)
    conversations.js         #   list / open / rename / delete
    projects.js              #   list / create / switch / delete projects
    documents.js             #   upload, poll status, delete
    memory.js                #   project memory entries
    models.js                #   GET /api/models
    settings.js              #   GET/PUT /api/settings
    admin.js                 #   GET/DELETE /api/admin/users (key -> user), GET /api/admin/system-prompt

  icons/
    icons.js                 # named SVG factories: Icon('search'), ThemeGlyphs, ... (one glyph, one place)

  components/
    app-shell.js             # 3-col .app grid + layout state classes + scrim

    ui/                      # design-system primitives - reused everywhere
      button.js  pill.js  badge.js  switch.js  seg-toggle.js
      menu.js  dropdown.js  modal.js  toast.js  progress-bar.js

    sidebar/
      sidebar.js             # column container + responsive drawer; the brand header and
      │                      #   new-chat button are single-use leaves and live in here
      project-picker.js      # switch/create/delete projects (configuration section 2)
      convo-list.js          # grouped (Today/Yesterday/...) conversation rows
      convo-item.js          # one .convo row (title + hover-reveal trash)
      user-chip.js           # avatar + name + auth line + settings button

    main/
      top-bar.js             # collapse btn - title - theme - model picker - panel toggle
      conv-title.js          # editable conversation title
      tools-menu.js          # composer dropdown of tool toggles (RAG/Search/Sandbox/Todos/Clock
      │                      #   + Canvas - one switch for the make/edit/read artifact trio) + "Use my docs"
      theme-toggle.js        # sun/moon - glyph swap driven by tokens, not JS theme checks
      model-picker.js        # dropdown menu + model items + capability badges
      message-stream.js      # the scrolling thread
      message.js             # user/bot message - activity dropdown (thinking + tools), citations,
      │                      #   footnotes, streaming caret, artifact chips, copy/retry + token stats
      tool-card.js           # tool-call card: expandable, status, doc-hits, stdout, todo checklist, errors
      composer.js            # textarea (autogrow) + attachments + tools + send + hint
      context-ring.js        # context-window usage circle in the composer

    settings/
      settings-modal.js      # user settings popup (theme, reply language, default provider) - opened from the user chip

    canvas/
      canvas-panel.js        # right pane container + drawer behaviour
      canvas-bar.js          # artifact tab strip + bar tools (KB toggle, expand, close)
      artifact-tabs.js       # the tab list
      artifact.js            # polymorphic dispatcher: picks a viewer by artifact kind
      artifacts/
        markdown-artifact.js #   sanitized render (lib/markdown.js) + source view (MdCode)
        html-artifact.js     #   sandboxed <iframe srcdoc> (lib/artifact-sandbox.js) + source (MdCode)
        svg-artifact.js      #   sandboxed iframe - SVG can carry script (F3) + source (MdCode)
        code-artifact.js     #   highlighted lines + Problems gutter (lib/highlight.js) + source (MdCode)
        md-math.js           #   MdMath({latex,display}) - the math leaf: wraps smath.renderMath -> <span class="md-math"> + native <math> (F4)
        md-code.js           #   MdCode({code,lang,gutter}) - the code leaf: wraps highlight -> <pre data-lang><code> tok-* spans (F4)
      knowledge-panel.js     # kb-view: header + privacy note + use-in-chat switch
      drop-zone.js
      doc-list.js
      doc-row.js             # file icon + meta + status pill (ready/processing/failed) + trash

  pages/                     # routed views (the router swaps these into the main region)
    chat.js                  # default - the conversation screen (/, /c/:id)
    admin/
      users.js               # /admin/users - admin-only user list + model-prompt inspector

  styles/                    # the global cascade - only rules no single component owns (style guide section 2)
    tokens.css               # design tokens - light-dark(Manila, Ember), [data-theme] scheme pins; loads first
    base.css                 # reset, body, focus ring, scrollbars, keyframes, reduced-motion guard
    layout.css               # .app grid, collapse states, responsive drawers + ALL @media
    primitives.css           # shared bare-class utilities (.btn, .ghost, .trash, .field ...)
```

> The app is mostly one screen (`chat`); `admin/users` is the only other true page.
> **Settings is a modal**, not a route - `components/settings/settings-modal.js`,
> opened from the user chip. Deep links work because the server routes unknown
> paths to `index.html` via `MapFallbackToFile` ([tech-stack](tech-stack.md)).

---

## 3. The four layers

Dependencies point **one way**: `components` -> `state` + `services` -> `lib`. The same
inward-only discipline the .NET side enforces ([principles](principles.md)), applied to
the front end.

```
  pages/        compose components into a routed screen
     │
     ▼
  components/   pure: props + state in -> DOM node out. Never fetch.
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

The authoring rules live in the [SPA style guide](spa-style-guide.md) - the
`component({ name, css, view })` factory, kebab-case filenames with PascalCase
exports, token rules, no local `@media`, list rendering, formatting. The layout-level
rules that belong here:

1. **One component per file**, in the `components/<area>/` that owns it. Co-locate a
   trivial subcomponent; promote it to its own file once reused or past ~40 lines
   (e.g. the brand header lives inside `sidebar.js`).
2. **Named exports only** - no `default`. Keeps imports greppable and the import graph flat.
3. **No top-level side effects** (no `fetch`, no global mutation at import time) -
   a component module must be importable by the test harness without booting the app.
4. **I/O through `services/`, state through `state/`.** A click handler calls a service;
   it does not build a `Request`.

---

## 5. Cross-cutting concerns

### Styling - tokens global, component CSS co-located
The global cascade is only what no single component owns
([style guide section 2](spa-style-guide.md#2-theming---derive-everything-from-global-tokens)):

| File | Holds |
|------|-------|
| `tokens.css` | every design token, defined once with `light-dark(manila, ember)`; the `[data-theme]` scheme pins; **must load first** |
| `base.css` | reset, `body`, the global `:focus-visible` ring, scrollbars, `@keyframes`, the `prefers-reduced-motion` guard |
| `layout.css` | the `.app` grid, collapse/wide states, responsive drawers, scrim - and **all `@media`** |
| `primitives.css` | shared bare-class utilities (`.btn`, `.ghost`, `.trash`, `.field` ...) applied by class string across many components |

Everything else ships **inside the component** via the `component()` factory, which
adopts the CSS as a Constructable Stylesheet - CSP-clean under `style-src 'self'`
(no `unsafe-inline`), and cascading after the globals so `layout.css` keeps priority.

### Theme
Two themes - **Manila** (paper light) and **Ember** (refined dark). Color tokens are
defined once with `light-dark()`; the default `:root` follows the OS via
`color-scheme: light dark`, and the explicit `[data-theme="manila"|"ember"]` scopes
just pin the scheme. `state/ui.js` owns the toggle: it sets
`documentElement[data-theme]` and persists to `localStorage` as a first-paint cache -
the server-side setting is the cross-device truth
([configuration section 3.1](configuration.md#31-theme)).

### Icons
`icons/icons.js` exposes every SVG glyph once as a named factory -
`Icon("trash", { size: 14 })` - so a glyph is defined in exactly one place.

### State stores
Scalar UI state uses VanJS `van.state` / `van.derive`. Keyed collections that churn -
the conversation list, the doc list, the message stream - hold **VanX `reactive`** row
objects, so a streamed token or a single doc's status change re-renders just that node;
list *membership* changes re-render via a map-rebuild binding (`vanX.list` keyed
rendering is the documented opt-in for hot lists, not yet adopted -
[style guide section 4](spa-style-guide.md#4-lists---reactive-rows-rebuild-on-membership)).

### Routing
`lib/router.js` is a tiny History-API router. The full route table (declared once, in
`app.js`): `/` and `/c/:id` -> `pages/chat.js`, `/admin/users` -> `pages/admin/users.js`.
Settings opens as a modal from the user chip - deliberately not a route.

### Streaming
`services/chat.js` POSTs the message (a **202 detached turn** -
[rest-api](rest-api.md#sending-a-message-detached-turn)), then consumes the
conversation's TurnEvent stream over the best available transport - **SSE, then range
polling**, both gap-free over the same `seq` cursor - and pushes each
event onto `state/chat.js` (+ `state/artifacts.js`). `message.js`, `tool-card.js`, and
the canvas bind to that state, so the typewriter effect, tool-card progress, and
artifact tabs are just reactive renders of incoming events.

### Security: token handling & rendering
The SPA holds a bearer token **and** renders untrusted model output, so the two are kept apart by
construction (full rationale in [security](security.md#3-findings--remediations)):

- **Token in memory only.** `services/auth.js` keeps the access token in a module variable, **not
  `localStorage`** - so an injected script has nothing persistent to read ([security F2](security.md#3-findings--remediations)).
  `state/auth.js` holds only displayable identity claims. If a refresh token must survive a reload,
  it lives in an `httpOnly; Secure; SameSite=Strict` cookie, never JS-readable storage. (The
  Content-Security-Policy in [operations](operations.md#http-security-headers--csp) is the outer
  wall; this is defence-in-depth behind it.)
- **Artifacts render in a sandboxed iframe.** Both `html-artifact.js` **and** `svg-artifact.js` use
  `<iframe srcdoc sandbox="allow-scripts">` - crucially **without `allow-same-origin`** (the two
  together would defeat the sandbox) - with a restrictive per-document CSP built by
  `lib/artifact-sandbox.js`. SVG gets the same treatment as HTML because inline `<svg>` can carry
  `<script>`/`onload` that would otherwise run in the app origin and steal the token
  ([security F3](security.md#3-findings--remediations)). The **Source** view shows raw text, never a
  live injected node.
- **Markdown is sanitized by construction.** `lib/markdown.js` is a **thin facade** over an in-house renderer
  (smd2 lineage) split across `lib/render/`: it wires `parse -> render -> assignHeadingIds` inside one
  `try/catch` (any fault degrades to literal source, so `renderMarkdown` is **total**) and re-exports the public
  surface - `renderMarkdown(src) -> DocumentFragment`, `sanitizeUrl(url) -> string`, and the closed `NODE_TYPES`
  set (identity preserved). The pipeline:
  - `render/lines.js` - **one declarative `LINE_KINDS` table** drives block classification: `classifyLine(line,
    lookahead, depth)` runs **once** per line and feeds **both** the block dispatcher **and** the
    paragraph-interrupt, so a line can never be read two ways. The bounded block parser (`MAX_NEST = 32`; past
    the cap a would-be container is plain text) builds a markdown AST in which **math and code are opaque leaves**
    carrying raw latex/code + lang/display.
  - `render/inline.js` - the O(n) left-to-right inline scanner (links, emphasis, code, entities, autolinks),
    bounded by `MAX_INLINE`/`MAX_DEST`/`MAX_TITLE` so unbalanced delimiters degrade to text, never recurse.
  - `render/dom.js` - the **structural renderer**: it emits every markdown element through **one guarded
    `createEl(ns, tag, attrs)` chokepoint** over a **closed per-`(ns, tag)` allow-list** (each tag's permitted
    attribute set is pinned; `href` only on `<a>`, `src` only on `<img>`) with a **fail-closed throw** on any
    unknown `(ns, tag)` or attribute - so the emitted DOM is a fixed allow-list and `innerHTML` is **never** used.
    `sanitizeUrl`/`sanitizeImgUrl` + `rel`/`target` are applied **locally at the link/image nodes** (sink-side).
    `MAX_INLINE = 32` bounds inline-container nesting here too (past the cap an emph/strong/del/link degrades to
    flattened text). For a math/code leaf the renderer **calls a VanJS component** (`MdMath`/`MdCode`) and inserts
    the returned DOM - it never reaches into smath/highlight itself.
  - `render/url.js` - the **single source** for `sanitizeUrl` (`javascript:`/`data:`/`vbscript:` plus control-char
    and `&colon;` smuggling collapse to `#`), `sanitizeImgUrl` (inline `data:image/(png|jpe?g|gif|webp|avif|bmp|
    x-icon);base64` **only**; every other url-shaped `src` -> `#`), `isExternal`, and `slugify`. External links get
    `rel="noopener noreferrer" target="_blank"`. `lib/markdown-links.js` imports `isExternal` from here (one copy).

  VanJS text bindings escape by default; the `render/` graph plus the `MdCode`/`MdMath` leaves are the only "render
  rich text" paths ([security F4](security.md#3-findings--remediations)). The **external-link confirm step lives
  outside** the pure renderer: `lib/markdown-links.js` exports `attachLinkConfirm(host)` - one delegated click
  listener per rendered body (not per `<a>`) that opens Gert's Modal before an external link leaves the app, wired
  in `components/main/message.js` and `components/canvas/artifacts/markdown-artifact.js` (kept out of the renderer
  the same way `lib/action.js`'s toast reach is). Headings carry a GitHub-style slug `id` (folded to `[a-z0-9_-]`,
  deduped `-1`/`-2` within the fragment) via `assignHeadingIds`, a **DOM post-pass** that reads `textContent`, so
  in-document `[x](#slug)` links resolve.
- **Code and math are VanJS sub-language components.** The two opaque leaves render through
  `component({ name, css, view })` ([style guide section 1](spa-style-guide.md#1-the-component-shape)) - the same
  factory as the ~30 other components, so each adopts its CSS once via a Constructable Stylesheet (CSP-clean under
  `style-src 'self'`) and its `view(props)` returns standard DOM built with `createElement`/`createElementNS` (never
  `van.tags`, which has no allow-list, and never `innerHTML`). The output DOM shape is **unchanged**, so existing
  selectors/CSS/consumers all still hold:
  - **`MdCode({ code, lang, gutter })`** (`md-code.js`) wraps `lib/highlight.js` -> `<pre data-lang><code>…tok-*
    spans…</code></pre>` where `<code>` holds **only** inert `tok-*` spans + text (highlight tints from
    `textContent`, never an HTML string; no attribute but `class`, no class outside `tok-*`). `data-lang` is the
    fence language for the chrome label, guarded to `/^[\w+#.-]{1,16}$/` and only ever set in `dataset`. The
    optional `gutter:true` path uses `highlightLines` for one `<code>` line-run per source line; the default is
    byte-for-byte the old inline code block. The four artifact source views (`code`/`markdown`/`html`/`svg`) and the
    chat code fence all flow through `MdCode`. `highlight.js` tints the fence languages the assistant emits (json,
    python, js/ts, c#/c/c++, rust, **go**, **bash/shell**, html/xml, markdown) and degrades unknown languages to
    plain text.
  - **`MdMath({ latex, display })`** (`md-math.js`) wraps `lib/smath.js`'s `renderMath` -> `<span class="md-math">`
    (or `"md-math md-math-display"`) wrapping a **native `<math>`** element. smath keeps its **closed
    `MML_ELEMENTS` allow-list** (`toDom`, built with `createElementNS`) and its **per-formula `try/catch`** so bad
    TeX degrades to literal text **per formula**, never document-wide. The `.md-math*` + `<math>` CSS moved into the
    component's adopted sheet; the renderer's own `.md-math-block` scroll wrapper stays in `styles/base.css`.
- **Math holds the same line - no third-party engine.** `lib/smath.js` is our own **zero-dependency** TeX -> native
  `<math>` MathML converter (not Temml, not KaTeX). A linear lexer (O(n), no ReDoS) feeds a bounded recursive descent
  (`MAX_DEPTH = 32`, `MAX_NODES = 6000`, `MAX_TEX = 8192`; past a bound it degrades to literal source) that is
  **total** over the closed `MML_ELEMENTS` allow-list - **never** `innerHTML`. The browser renders the emitted
  `<math>` natively (MathML Core: Firefox, Chromium 109+/Jan 2023, WebKit). There is no `trust`/`throwOnError` to set
  because there is no third-party engine: unknown control words degrade to a visible `<mtext>`, and the **only**
  attributes set are inert MathML presentation hints (`mathvariant`, `stretchy`, `fence`, `accent`, `displaystyle`,
  `movablelimits`, `width`, `mathcolor`) - **no `href`/`src`/`style` sink**, so math cannot navigate, fetch, or
  script, and emits no inline `style` (CSP-safe under `style-src 'self'`, nothing to mirror or strip). Colour
  (`\color`/`\textcolor`) rides the `mathcolor` *attribute* (charset-validated, never a `style`), and chemistry
  (`\ce`/`\pu`, an mhchem subset: subscripts, charges, `->`/`<=>` arrows, states) lowers onto the **same** leaves -
  both inside the closed allow-list, not exceptions to it. The converter recognises math
  only on a **closed** delimiter pair and length-caps the inline scan, so streaming stays literal-until-complete.
  `\[...\]` is **block-level only** (a line that opens with `\[`), so a mid-line `\[escaped\]` stays a literal bracket
  escape, not math ([security F4](security.md#3-findings--remediations)). Because no vendored third-party file touches
  model output, there is nothing here for a dependency scanner to miss.

---

## 6. Dev/release pipeline (no npm)

The whole point of native ESM: **the dev build is "no build."** Minification is a
publish-time concern handled entirely by .NET.

### Development - raw source
- `Gert.Api` serves `wwwroot/` directly in `Development` (`UseStaticFiles`,
  no-cache, `MapFallbackToFile("index.html")`).
- The browser loads `app.js` as a module and resolves every `import` by path. Every import is
  an **absolute same-origin path** - there are no bare specifiers, so there is **no import map**
  and nothing inline for the CSP to allow:

```html
<script type="module" src="/app.js"></script>
```

- This is what keeps the CSP a plain `script-src 'self'` with **no `sha256` hash** to maintain:
  with no inline `<script>` there is nothing to hash, so `SecurityHeadersMiddleware.cs` carries no
  import-map hash constant and edits to the SPA never force a recompute.
- What you see in devtools **is** the file on disk - real names, real line numbers, no
  source maps. Edit, refresh, done.

### Release - minify in place, on `dotnet publish`
An MSBuild target runs **[NUglify](https://github.com/trullock/NUglify)** (pure .NET,
NuGet, **no npm**) over the assets as they land in the publish output, minifying each
`.js`/`.css` **to the same relative path** (`tools/Gert.Web.Minify`, invoked from
`Gert.Api.csproj` after publish). Because paths don't change, the ESM import graph keeps
resolving - we minify, we don't bundle or rename. The minifier only touches `.js`/`.css`,
never `.html`, so there is no inline-script hash for it to invalidate.

**ESM caveat - validated.** NUglify must parse modern module syntax; this was verified
against the real `wwwroot` on `dotnet publish` (the published graph
still resolves). The minify-in-place, **no-bundle** design remains the safety net: a
file that ever trips the parser stays raw (or whitespace-minified) without breaking
the import graph - a single file failing never cascades.

**Cache-busting:** keep filenames stable (so imports don't need rewriting) and bust via
HTTP - ETags plus versioned query strings on the `index.html` entry tags. Content-hashed
filenames are deliberately avoided here because they'd force rewriting every relative
`import`.

**If we ever want bundling/fingerprinting** without npm: `LigerShark.WebOptimizer.Core`
(also pure .NET / NUglify, runtime pipeline) is the drop-in upgrade - deferred, since the
no-build ESM model is simpler and HTTP/2 multiplexing makes many small modules cheap.

---

## 7. Feature -> component map

Every interactive piece of the app, and where it lives.

| Feature | File(s) |
|----------------|---------|
| 3-column grid, collapse/wide states, mobile drawers, scrim | `components/app-shell.js`, `styles/layout.css` |
| Brand header + new chat | inside `components/sidebar/sidebar.js` (single-use leaves) |
| Project picker (switch/create/delete) | `components/sidebar/project-picker.js` |
| Grouped conversation list | `components/sidebar/convo-list.js` + `convo-item.js` |
| User chip (avatar, auth line, settings) | `components/sidebar/user-chip.js` |
| Top bar shell | `components/main/top-bar.js` |
| Editable conversation title | `components/main/conv-title.js` |
| Tool toggles (RAG / Search / Fetch / Sandbox / Todos / Clock / Ask me / Memories / Sub-agents + the Canvas trio as one switch) | `components/main/tools-menu.js` (composer dropdown) |
| Theme toggle | `components/main/theme-toggle.js` + `state/ui.js` |
| Model (provider) picker dropdown + capability badges | `components/main/model-picker.js`, `components/ui/dropdown.js`, `badge.js` |
| User settings (theme, reply language, default provider) | `components/settings/settings-modal.js` |
| Message thread + user/bot messages (right-aligned user bubble, no role headers) | `components/main/message-stream.js` + `message.js` |
| Activity dropdown (thinking + tool cards behind one summary), citations, footnotes, streaming caret, artifact chips, copy/retry actions + token stats | inside `components/main/message.js` |
| Tool-call cards (expandable, status, doc-hits, stdout, todos, errors) | `components/main/tool-card.js` |
| Composer (autogrow, attachments, tools, send) | `components/main/composer.js` |
| Context-window usage ring | `components/main/context-ring.js` |
| Canvas tab strip + bar tools | `components/canvas/canvas-bar.js` + `artifact-tabs.js` |
| Markdown / HTML / SVG / Code artifacts | `components/canvas/artifacts/*.js` via `artifact.js` |
| Markdown math / code leaves (the renderer's sub-language components) | `components/canvas/artifacts/md-math.js` (smath), `md-code.js` (highlight) |
| Knowledge panel (privacy, use-in-chat switch) | `components/canvas/knowledge-panel.js`, `components/ui/switch.js` |
| Drop zone + doc list + status pills + trash | `components/canvas/drop-zone.js`, `doc-list.js`, `doc-row.js`, `components/ui/pill.js` |
| Toasts / modals / progress | `components/ui/toast.js`, `modal.js`, `progress-bar.js` |
| Admin user list | `pages/admin/users.js`, `services/admin.js` |

---

## 8. Decisions & open choices

- **VanX for reactive collections - decided, shipped.** `van-x` is vendored in `lib/`
  and every keyed collection's rows - the conversation list, the doc list, the message
  stream - are `vanX.reactive`. The ~1 KB buys per-field updates within a row;
  membership changes currently re-render via map-rebuild bindings, with `vanX.list`
  per-item keyed rendering the opt-in upgrade
  ([style guide section 4](spa-style-guide.md#4-lists---reactive-rows-rebuild-on-membership)).
  Plain `van.state` is the tool for scalar UI state in `state/ui.js` (theme, layout flags).
- **Minifier validation - resolved.** NUglify minifies the real ESM source cleanly;
  verified on `dotnet publish`. The raw-fallback safety net stays in place.
- **i18n / UI language - not built.** The settings design reserves a UI-language
  setting ([configuration section 3.2](configuration.md#32-language)); locale dictionaries
  and an i18n pass on component strings would be the additive follow-up.
