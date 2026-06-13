# VanJS component style guide

How to **write** a component for the Gert SPA (`src/Gert.Api/wwwroot`). The guiding idea:
**every component is a pure function that bundles three concerns - style, content, and
logic - and all theming + responsiveness is driven by global CSS tokens.**

This is the *conventions* half of the front-end docs; the *map* half - where files live,
the four layers, the dev/release pipeline - is [ui-components.md](ui-components.md).

Dependencies: [VanJS](https://vanjs.org) and [VanX](https://vanjs.org/x) (reactive
objects/arrays), **vendored** in `lib/van.js` / `lib/van-x.js` and imported by the bare
specifiers `van` / `van-x` through the import map in `index.html` (no npm -
[ui-components section 6](ui-components.md#6-devrelease-pipeline-no-npm)).

---

## 1. The component shape

Components are just functions, but we give them a consistent shape with a small
`component()` factory (`lib/component.js`). It maps directly onto the three concerns:

| Concern   | Where it lives                          |
|-----------|------------------------------------------|
| `style`   | the `css` string (adopted once)          |
| `logic`   | everything before `return` in `view`     |
| `content` | the tag tree you `return`                |

```js
// lib/component.js (the real implementation)
const injected = new Set();

// Adopt a stylesheet built from a CSS string. Reusable by non-component modules
// that need to ship CSS under a strict CSP (e.g. the imperative toast host).
// CSP: styles go through a Constructable Stylesheet (document.adoptedStyleSheets),
// NOT an inline <style> element - CSSOM construction is exempt from `style-src`,
// so a strict `style-src 'self'` holds with no 'unsafe-inline' / nonce / hash.
// Adopted sheets cascade AFTER the <link>ed globals, so layout.css keeps
// priority for responsive overrides.
export const adoptStyles = (css) => {
  const sheet = new CSSStyleSheet();
  sheet.replaceSync(css);
  document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
};

export const component = ({ name, css, view }) => (...args) => {
  if (css && !injected.has(name)) {
    injected.add(name);
    adoptStyles(css);
  }
  return view(...args);
};
```

### Authoring a component

```js
// components/sidebar/convo-item.js - one .convo row (trimmed).
import van from "van";
import { component } from "../../lib/component.js";
import * as chat from "../../state/chat.js";
import { navigate } from "../../lib/router.js";

const { div, span } = van.tags;

export const ConvoItem = component({
  name: "convo-item",
  css: `
    .convo{position:relative; padding:8px 10px 8px 8px; border-radius:var(--r-sm); cursor:pointer; display:flex; align-items:center; color:var(--ink-2); transition:.14s;}
    .convo:hover{background:var(--surface-2); color:var(--ink);}
    .convo.active{background:var(--chat-on); color:var(--coral-deep); font-weight:600;}
  `,
  view: (convo) =>
    div(
      {
        class: () => "convo" + (chat.activeId.val === convo.id ? " active" : ""),
        // Navigate only - ChatPage opens the thread for its route.
        onclick: () => navigate("/c/" + convo.id),
      },
      span({ class: "t" }, () => convo.title || "Untitled"),
    ),
});
```

### Rules

- **One component per file.** Filename in `kebab-case.js` matching the factory `name`
  (`convo-item.js` -> `name: "convo-item"`); the exported factory is **PascalCase**
  (`ConvoItem`), so call sites read like JSX: `ConvoItem(convo)`. Imperative *openers*
  - functions that mount transient UI rather than return a node - are camelCase verbs:
  `openSettings()`, `openModelSettings(model)`, `toast(msg)` (see section 10).
- **Named exports only** - no `default`. Keeps imports greppable and the import map flat.
- The **root element gets one root class, unique app-wide**, declared in the component's
  `css` string; **all of the component's CSS is namespaced under it.** A short form is
  the norm (`tool-card` -> `.tcard`, `dropdown` -> `.dd`, `convo-item` -> `.convo`); the
  full kebab name is fine too - **uniqueness is the requirement, not the spelling.**
  The factory `name` stays the kebab-case file name regardless (it keys the
  inject-once set). Shared primitives applied by bare class string (`.btn`, `.field`)
  live in `primitives.css` instead - see section 2.
- Keep `logic` (state + handlers) above `content`. A blank line and a comment
  marker between them is enough; don't split into separate functions unless the
  logic is genuinely reusable.
- **Components** take a **single argument** - a props object, or a plain value when
  there's only one input (`ConvoItem(convo)`, `Message(m)`). Always default the object
  itself (`view: ({ ... } = {}) =>`); default individual props where a fallback is
  meaningful, and document *required* props in the leading comment (the house habit -
  see `modal.js`, `dropdown.js`). Non-component helpers (`Icon(name, opts)`) are exempt.
- **No top-level side effects** - no `fetch`, no global mutation at import time. I/O goes
  through `services/`, state through `state/` ([ui-components section 3](ui-components.md#3-the-four-layers)).
- **Non-obvious code cites its design doc** - a `//` comment naming the doc section or
  security finding (`F2`, `F3`, `F4`...) it implements. Code comments and docs reference
  each other; keep both ends accurate when either changes.

---

## 2. Theming - derive everything from global tokens

This is the heart of the guide. **Component CSS may never hardcode a color - anywhere,
including box-shadows.** Colors only ever appear as `var(--token)`; all real values live
in `styles/tokens.css`. This single rule gives you both themes *and* responsiveness
for free (see section 3).

What must be a token, and what may stay literal:

| Value | Rule |
|-------|------|
| Colors - fills, text, borders, shadows, scrims | **Always a token.** No exceptions. Shadows use `var(--lift)` or a `--shadow-*` token, scrims a `--scrim*` token - never a literal `rgba(...)`. |
| Radii and shared rhythm - `--r`, `--r-sm`, `--r-xs`, `--r-lg`, `--head-h` | **Always a token.** These align components to each other; a literal copy drifts. |
| Font sizes and line heights - the `--fs-*` / `--lh-*` scale | **Use the scale.** Seven steps (`--fs-2xs` 10px -> `--fs-xl` 24px) plus `--lh-tight/-ui/-reading`; a new size off the scale needs a reason (the only shipped exception is the 7.5px artifact-tab type glyph). |
| Transition timing - `--t-fast`, `--t-slow`, `--ease` | **Always a token.** `var(--t-fast)` for hovers/reveals, `var(--t-slow) var(--ease)` for drawers/menus/modal rise. The `prefers-reduced-motion` guard in `base.css` neutralizes both. |
| Component-internal one-off spacing - an `8px 10px` padding, a `312px` menu width | **May be literal**, though menu/list rows share `var(--sp-2) var(--sp-3)` and the `--sp-1...--sp-6` scale (4/8/12/16/24/32px) is preferred where it fits. Per-component geometry that nothing else aligns to doesn't earn a token. |

The boundary: **if a value would need to change at a breakpoint or between themes, it
must be a token** (or a `layout.css` override - section 3). If it's just this component's own
geometry, a literal is fine.

The non-color token families at a glance (all in `styles/tokens.css`):

| family | values | used for |
|--------|--------|----------|
| `--fs-2xs/-xs/-sm/-md/-base/-lg/-xl` | 10 / 11 / 12.5 / 13.5 / 15 / 18 / 24px | mono micro-labels - meta - dense UI - default UI (body) - reading text - headings - page h1/brand |
| `--lh-tight/-ui/-reading` | 1.35 / 1.5 / 1.62 | headings - UI text - bot prose |
| `--sp-1...--sp-6` | 4 / 8 / 12 / 16 / 24 / 32px | row padding, modal padding, gutters |
| `--r-xs/--r-sm/--r/--r-lg` | 6 / 8 / 12 / 16px | chips - buttons/rows - cards - composer |
| `--t-fast`, `--t-slow`, `--ease` | .14s / .28s / `cubic-bezier(.2,.8,.2,1)` | every transition/reveal |
| `--grain-img` | the paper-grain dot layer | pane backgrounds (`.sidebar`/`.main`/`.panel`) paint it with `background-size:18px 18px` so grain sits *under* content |

> The overlay shadows, scrim chips, and the brand mark's `#bf4727` live in `tokens.css`
> as `--shadow-*`, `--scrim-*`, and `--brand`. Reach for those tokens - don't embed
> literals in components.

The two themes are **Manila** (paper / editorial light) and **Ember** (refined dark).
Every color token is defined **once** with `light-dark()`, and the document rides
`color-scheme`:

```css
/* styles/tokens.css - every color token defined exactly once */
:root{
  color-scheme: light dark;                       /* no explicit choice -> follow the OS */
  --r:12px; --r-sm:8px; --r-xs:6px; --r-lg:16px;
  --fs-md:13.5px; /* ...the 7-step type scale + --lh-*, --sp-*, --t-* live here too */
  --bg:    light-dark(#f4ede1, #16110e);
  --ink:   light-dark(#3a2c20, #efe7df);          /* primary text */
  --ink-2: light-dark(#6f5d4c, #b3a79c);          /* secondary */
  --coral: light-dark(#dd5728, #ff6b3d);          /* the accent */
  /* ... */
}

/* The toggle just PINS the scheme; light-dark() does the rest. */
[data-theme="manila"]{ color-scheme: light; }
[data-theme="ember"] { color-scheme: dark; }
```

**One warm accent.** Every highlight - active states, toggles, counts, done-marks,
selection - rides the coral family (`--coral`, `--coral-deep`, `--coral-soft`,
`--coral-line`) in both themes. Green (`--green`) is *status only* (the model-online
dot); don't reintroduce it for interactive or "success" states, and reserve
`--brick`/`--fail-*` for errors. Filled accent surfaces always pair with
`--on-accent` (AA in both themes, see `primitives.css` `.btn`).

Toggling the theme is owned by `state/ui.js` - it is the **only** module that may touch
`documentElement[data-theme]` or the `gert.theme` localStorage key (its `setTheme` /
`applyServerTheme` are the only mutators). It persists the choice to `localStorage` as a
first-paint cache; the server-side setting is the cross-device truth
([configuration section 3.1](configuration.md#31-theme)) and is applied at boot via
`ui.applyServerTheme` when the initial prefs load lands. Components never know which
theme is active - they read tokens.

> Even *conditional rendering* by theme is done with tokens where possible: the
> sun/moon glyph swap in `theme-toggle.js` is driven by `--sun-display`/`--moon-display`
> tokens flipped in `tokens.css`, so the component itself stays theme-blind.

### What legitimately stays global

A component's `css` owns the rules namespaced under its own root class. Four kinds
of rule belong in the global stylesheets (`styles/*.css`) instead, because they aren't
owned by any one component:

- **Tokens** (`tokens.css`) - all color/shared-value tokens + the theme scopes. Must load first.
- **Reset & document chrome** (`base.css`) - body, the global `:focus-visible` ring,
  scrollbars, keyframes, and the `prefers-reduced-motion` guard. (Motion is a global
  concern like the reset: components reference `--t-*`, base.css neutralizes them.
  The paper grain itself is painted by the pane components via `--grain-img` so it
  stays under their content.)
- **App-frame layout + responsiveness** (`layout.css`) - the 3-column `.app` grid,
  collapse states, drawers, and **all `@media`** (see section 3) - `@media` for *motion*
  being base.css's one carve-out above.
- **Shared utilities applied by bare class-string** (`primitives.css`) - e.g.
  `.btn`, `.ghost`, `.trash`, form/`.field` scaffolding. These are used across many
  components and have no single owner; a component using `class:"btn"` may render
  before any `Button` does, so the rule can't co-locate without a flash.

Everything else co-locates. When a parent styles into a child it composes
(`.model-picker.open .menu`), that rule lives with the **parent** - it owns that
relationship, and the higher specificity makes it order-independent of the child's
base rule.

---

## 3. Responsiveness - media queries are global

**Components never write `@media`.** All responsive behaviour lives in
`styles/layout.css` (plus the OS-fallback block in `tokens.css`), which works two ways:
breakpoints **rewrite the layout variables** the grid is built from, and they
**override component rules under the `.app` prefix**:

```css
/* styles/layout.css - the ONLY place media queries appear */
.app{--nav-col:264px; --panel-col:var(--panel-w,372px); display:grid;
     grid-template-columns:var(--nav-col) minmax(500px,1fr) minmax(0,var(--panel-col));}
.app.nav-collapsed{--nav-col:0px;}

@media (max-width:1079px){
  .app{grid-template-columns:1fr !important;}
  .app .sidebar,.app .panel{position:fixed; /* ...slide-over drawers... */}
  .app .menu{width:312px; max-width:calc(100vw - 28px);}
}
```

The `.app` prefix is load-bearing: component CSS is adopted *after* the `<link>`ed
globals (section 1), so a bare `.panel{...}` in `layout.css` would lose to the component's own
`.panel` rule. Prefixing raises specificity so the responsive override wins regardless
of injection order - `layout.css` says so in its header comment.

The breakpoint values themselves live in `layout.css`. The one JS copy -
`ui.isMobile()`'s `(max-width:1079px)` in `state/ui.js` - must be kept in sync by hand;
don't add more.

> If a component genuinely needs a layout shift that can't be expressed as a
> `layout.css` override, that's a signal it should become two components selected by a
> global `breakpoint` state - not a local media query.

---

## 4. Lists - reactive rows, rebuild on membership

The shipped default: **collections are arrays of `vanX.reactive` row objects, rendered
by a plain map-rebuild binding.** Per-field updates re-render only the nested binding
that reads the field; **membership changes re-run the outer binding and rebuild every
row**:

```js
// components/main/message-stream.js - the house idiom
const thread = div(
  { class: "thread" },
  () => div(...chat.messages.map((m) => Message(m))),
);
```

How the two granularities split:

- **A field changes** (`m.text += delta`, `doc.status = "ready"`): only the inner
  binding that reads that field re-renders - a streamed token repaints one text node,
  a doc-status tick repaints one pill. This is why rows must be `vanX.reactive` and
  fields must be read *inside* a function: `span(() => convo.title)`.
- **Membership changes** (push/splice/replace): the outer binding re-runs and rebuilds
  **all** rows - O(n) per change, including re-running `renderMarkdown` for every
  message and recreating any iframes. That's acceptable while membership changes are
  user-paced (a send, an upload, a conversation switch) and lists are modest; it is
  *not* free, and side-effectful rows (iframes, tickets) make it worse.

When a rebuild is measurably hot, the opt-in upgrade is **`vanX.list`** - keyed
per-item insert/remove instead of a wholesale rebuild. **It is not currently used
anywhere in the tree**; the stores are already `vanX.reactive` arrays, so adopting it
is mechanical (the canvas viewers and the message thread are the first candidates):

```js
import * as vanX from "van-x";

vanX.list(ul, items, (item, remove) =>
  li(
    span(() => item.val),                // read item.val inside a function for reactivity
    button({ onclick: remove }, "no"),
  ),
)
```

Notes:
- With `vanX.list`, the item function's first param is a **state** - read it as
  `() => item.val`.
- **Do not alias** sub-fields of a reactive object into other variables; it breaks
  VanX's dependency tracking. (The codebase respects this today: whole reactive
  objects are passed around, fields are read inside bindings.)
- For static (non-reactive) lists, plain `.map` is fine: `ul(items.map(i => li(i)))`.
- Which collections are VanX-reactive (conversations, docs, the message stream) vs
  plain `van.state` scalars is decided per store - see section 7 and
  [ui-components section 5](ui-components.md#5-cross-cutting-concerns).

---

## 5. Routing

We use a tiny dependency-free **History-API** router (`lib/router.js`): real paths
(not hashes), pattern matching with `:params`, and same-origin `<a data-link>`
interception so links navigate without a full page load. It renders the matched
handler's node into a host element.

```js
// navigate("/c/123") - programmatic navigation (history.pushState + re-resolve).
// <a href="/admin/users" data-link>Users</a> - intercepted; no full reload.
import { navigate, mountRouter } from "./lib/router.js"
```

Routing rules are declared in one place, when the router is mounted - the whole
app map at a glance (this is the real route table from `app.js`):

```js
// app.js
mountRouter({
  host: mainHost,                       // the element pages render into
  render: (node) => mainHost.replaceChildren(node),
  routes: (route) => {
    route("/",            () => ChatPage({}));
    route("/c/:id",       (p) => ChatPage(p));   // p.id is the matched param
    route("/admin/users", () => AdminUsersPage());
  },
})
```

> Params (`/c/:id`) and same-origin link interception are built in. Need guards or
> nested routes? Extend the matcher in `lib/router.js`. Keep routing rules
> declarative and in the one `mountRouter` call regardless. (Settings is a **modal**,
> not a route - only chat and the admin user list are true pages.)

---

## 6. Services - the shape of an API module

All `/api` traffic goes through `services/` - **no `fetch` (or `http.*` call) anywhere
else.** One module per REST resource, exporting CRUD-verb functions
(`list / create / rename / remove / open / select`), each with the same spine:
**call `http.*` -> mutate the store -> return the payload.**

```js
// services/conversations.js (trimmed)
import * as http from "./http.js";
import * as chat from "../state/chat.js";

const pid = () => chat.activeProjectId.val;   // every project-scoped service has this

export const rename = async (id, title) => {
  await http.patch(`/projects/${pid()}/conversations/${id}`, { title });
  const c = chat.conversations.find((x) => x.id === id);
  if (c) c.title = title;
  if (chat.activeId.val === id) chat.setTitle(title);
};
```

The pieces:

- **`services/http.js` is the only fetch.** It exposes `get / post / patch / put /
  del / upload` (JSON in/out, snake_case wire shape), plus the streaming transports:
  `sse(path)` - an async generator parsing the EventSource wire format off a fetch
  body so the Bearer header rides along - and `ws(path)` - the token as the second
  `Sec-WebSocket-Protocol` entry, never in the URL ([security F2](security.md#f2---token-storage)).
- **Failures throw `ApiError { status, message, body }`** - shaped once in `http.js`'s
  `handle()`, so callers can branch on `e.status` (`services/chat.js` turns a 409 into
  "the previous response is still finishing").
- **The `pid()` helper.** Project-scoped services derive the project id from the store
  - `const pid = () => chat.activeProjectId.val;` - at the top of the module
  (`conversations.js`, `documents.js`, `memory.js`). Components never assemble
  `/projects/{pid}/...` paths.
- **Services may call sibling services** (`projects.select` -> `conversations.list`);
  they never import a component.
- **The sanctioned exception:** `services/auth.js`'s token endpoints bypass `http.js`
  deliberately (they *produce* the token `http.js` attaches) - documented in the file.
- Even a one-call resource earns a module: `services/artifacts.js` exists solely to mint
  the html-artifact preview ticket - components never call `http.get` inline.

---

## 7. Stores - the shape of state

`state/` modules hold the data, nothing else - with one sanctioned exception:
`state/ui.js` owns the `documentElement[data-theme]` attribute and the `gert.*`
localStorage preference keys (theme, panel width). No other store - and **no
component** - touches either.

What goes in which container:

- **`van.state`** for scalars: `activeId`, `streaming`, `theme`, layout flags.
- **`vanX.reactive`** for keyed collections and row objects: `conversations`,
  `messages`, `documents`, `tools` (so field updates re-render one node - section 4).
- **`van.derive`** for computed values: `models.selected`, `knowledge.totalBytes`.
  (Module-level derives in stores are fine - they're singletons. Derives in
  *components* have lifetime rules - section 12.)

Mutators follow two shapes:

- **Reset-style setters** - `setX(list)` empties in place (`length = 0`) then pushes,
  so existing bindings stay attached to the same reactive array.
- **`addX(item)` returns the reactive node** it pushed, so services can keep mutating
  it as events stream in.

```js
// state/chat.js (trimmed)
// Message shape (van-x reactive object pushed onto `messages`):
//   { id, role: "user"|"assistant", text, streaming,
//     tools: reactive([ { id, kind, status, ... } ]),
//     citations: reactive([ { ordinal, label, doc_id, locator } ]) }

export const setConversations = (list) => {
  conversations.length = 0;
  list.forEach((c) => conversations.push(c));
};

export const addAssistantMessage = () => {
  const m = reactiveMessage({ role: "assistant", text: "", streaming: true, working: true });
  messages.push(m);
  return m;
};
```

**Document the row shape in a comment at the top of the store** (as above, and
`state/knowledge.js`, `state/models.js`). With no TypeScript, these comments are the
SPA's type system - keep them current when a field is added.

---

## 8. Error handling - attempt() and toasts

Two tiers, by who's waiting:

1. **User-initiated actions** - wrap in `attempt(fn, "Couldn't <do the thing>")`
   (`lib/action.js`). It runs the action, toasts `kind:"err"` on failure, and returns
   `undefined` so callers can branch. Error copy starts **"Couldn't ..."**.
2. **Background/boot loads** (initial fetches, silent refresh, sidebar refreshes) -
   a bare `.catch(() => {})` is acceptable: nobody asked, nobody's waiting.

```js
// lib/action.js - the whole contract
export const attempt = async (fn, errorMessage = "Something went wrong") => {
  try {
    return await fn();
  } catch {
    toast(errorMessage, "err");
    return undefined;
  }
};
```

The rules that bite:

- **Nothing the user just asked for may fail silently.** A click handler either
  `attempt()`s or renders an error state. Swallowing a failed load into an empty list
  (so "broken" reads as "no data") is the anti-pattern.
- **Await when the next step depends on success.** `attempt` returns a promise;
  fire-and-forget is fine only when nothing downstream assumes the action worked:

  ```js
  const ok = await attempt(() => svc.remove(id), "Couldn't delete this chat");
  if (ok === undefined) return;   // failed - don't navigate away
  ```
- Stream errors don't toast - they render *into the message* (`services/chat.js`
  appends an `_Error: ..._` line to the assistant bubble), because the failure's home
  is the thread, not a corner notification.

---

## 9. Streaming - one cursor, three transports

`services/chat.js` is the canonical async service: read it before writing anything
stream-shaped. The contract ([rest-api -> Receiving a turn](rest-api.md#receiving-a-turn)):
POST the message (**202 + detached turn** - the server keeps generating even if every
client disconnects), then consume the conversation's TurnEvent stream over the best
transport available - **WebSocket, then SSE, then range polling** - all sharing one
`seq` cursor so a fallback resumes without gaps or duplicates.

```js
// services/chat.js - the ladder. Each consumer returns true = turn finished,
// false = transport unavailable, fall through. One cursor spans all three.
const consume = async (pid, cid, after, assistant, signal) => {
  const cursor = { seq: after };
  if (signal?.aborted) return;
  if (await consumeWs(pid, cid, cursor, assistant, signal)) return;
  if (signal?.aborted) return;
  if (await consumeSse(pid, cid, cursor, assistant, signal)) return;
  if (signal?.aborted) return;
  await consumePoll(pid, cid, cursor, assistant, signal);
};
```

The rules:

- **The cursor is a shared watermark.** `applyTurnEvent` applies an event only if
  `seq > cursor.seq` - that one check drops replay/live duplicates *and*
  transport-fallback overlap. Terminal events are a fixed set
  (`message_end`, `cancelled`, `error`).
- **Abort means "detach", and detach is terminal.** Each consumer treats
  `signal.aborted` as turn-over (`resolve(true)`) so a user stop never falls through
  to the next transport. The server turn keeps running - `detach()` (conversation
  switch) drops the client only; `stop()` POSTs a server-side cancel and stays
  attached for the terminal `cancelled` event; `resume(cid, msg)` re-attaches after a
  reload and replays the whole turn from the row's `seq`.
- **Ownership guards in `finally`.** The in-flight turn's `AbortController` and
  promise are module-level (`activeController` / `activeTurn`); every `finally` checks
  `activeController === ac` before restoring composer state, because a newer `send()`
  may already own it. A new `send()` **settles the previous turn first**
  (`Promise.race([activeTurn, sleep(10_000)])`) so a stop->send can't race the cancel
  into a 409.
- **Components never see a transport.** The consumer maps every event onto the
  reactive assistant message (`state/chat.js`) and `state/artifacts.js`; the
  typewriter, tool cards, citations, and canvas tabs are just bindings re-rendering.

---

## 10. Overlays - imperative by design

Transient UI is **not** store-driven. Modals and toasts mount themselves, clean
themselves up, and return control to the caller - the sanctioned exception to
"components return a DOM node":

- **`Modal({ title, body, onConfirm, actions, ... })`** (`components/ui/modal.js`)
  appends its scrim to `document.body` and **returns `close`**. The default footer is
  Cancel + a primary button; `actions(close)` is the escape hatch for custom layouts.
  `close()` removes the node *and* its document keydown (Esc) listener - the model
  citizen for section 12.
- **Feature modals are camelCase openers** - `openSettings()`,
  `openModelSettings(model)` in `components/settings/` - thin functions that build a
  body and call `Modal`. Settings is a modal, not a route (section 5).
- **`toast(message, kind)`** (`components/ui/toast.js`) appends into a lazily-created
  fixed host and auto-dismisses. Imperative modules that never go through
  `component()` adopt their CSS with `adoptStyles(CSS)` directly - same CSP-clean path.
- **Menus**: `Menu({ trigger, open, wrapClass, children })` - `open` is a
  **caller-owned `van.state`**; the wrapper renders `wrapClass + " open"` and the
  *caller's* CSS owns the `.open .menu` reveal (positioning per call site). The
  trigger toggles with `stopPropagation()`; a document click closes - the listener
  exists only while the menu is open (the section 12 pattern).
- **Snapshot primitives**: `ProgressBar({ value, max })` and `Pill` render a
  **snapshot** and stay binding-free - the *caller's* reactive binding re-renders them
  with fresh props. This is the house pattern for generic leaves (`progress-bar.js`'s
  header comment says so): primitives don't subscribe, callers do.

The z-index ladder (keep new layers consistent with it):

| 30 | 50 | 60 | 70 | 80 |
|----|----|----|----|----|
| menus (`menu.js`) | scrim (`layout.css`) | mobile drawers (`layout.css`) | modal (`modal.js`) | toast (`toast.js`) |

---

## 11. Security rules

The SPA holds a bearer token and renders hostile text in the same document. These
rules keep the two apart **by construction**; each maps to a finding in
[security section 3](security.md#3-findings--remediations). Don't weaken one without reading
its finding.

- **LLM output is hostile.** Always. So is anything derived from it (artifact names,
  citations, locators).
- **No HTML-string sinks, ever.** No `innerHTML` / `outerHTML` /
  `insertAdjacentHTML` / `DOMParser` / `document.write` anywhere in the SPA - the tree
  is grep-clean today; keep it that way. Rich text renders **only** through
  `lib/markdown.js` (+ `lib/highlight.js` for code), which build real DOM nodes from
  `textContent` ([F4](security.md#f4---markdown-sanitization)). The single
  markup-bearing sink permitted is a sandboxed `iframe.srcdoc` (next rule).
- **Untrusted markup runs only in a sandboxed iframe - never with
  `allow-same-origin`.** HTML/SVG artifacts go through the separate-origin ticket
  path or `lib/artifact-sandbox.js`'s `srcdoc` + per-document CSP
  ([F3](security.md#f3---svghtml-artifact-rendering)). `sandbox="allow-scripts"` at
  most; adding `allow-same-origin` would hand the artifact the app origin and the
  token. SVG counts as HTML here - it carries `<script>`/`onload`.
- **Every data-derived `href` goes through `sanitizeUrl()`** in `lib/markdown.js` -
  the single chokepoint that rejects `javascript:`/`data:`/`vbscript:` and de-smuggles
  control characters. Don't re-derive `^https?:` checks inline.
- **`blob:` URLs follow create -> use -> revoke.** The model is
  `artifact.js#downloadArtifact`: mint, click, `URL.revokeObjectURL` immediately. A
  blob URL left alive pins its bytes and is an openable same-origin document - never
  hand one to `window.open` without a revocation plan, and never build one from
  script-capable types (SVG) outside the sandbox.
- **The token lives in memory only.** A module variable in `services/auth.js`, read
  by `services/http.js` and nobody else - no component or state module imports
  `getToken` ([F2](security.md#f2---token-storage)). It never touches `localStorage` /
  `sessionStorage` / cookies / URLs / logs; WS auth rides the subprotocol, SSE rides a
  fetch header. `localStorage` is for non-secret prefs only, keys namespaced `gert.*`.
- **Styling stays CSP-clean by construction.** Component CSS through `adoptStyles`
  (Constructable Stylesheets are exempt from `style-src`), inline values through the
  `style:` prop (CSSOM). No inline `<style>`, `<script>`, or event-handler attributes
  - `style-src 'self'` / `script-src 'self'` never need loosening
  ([F1](security.md#f1---content-security-policy--security-headers)).

---

## 12. Cleanup discipline

VanJS garbage-collects bindings whose DOM is disconnected - but only what it knows
about. Everything else is yours to release:

- **A listener on `document`/`window` must be removed** when its component leaves the
  DOM. `Modal` is the model: `close()` removes its Esc keydown. The failure mode: a
  document click listener registered per render and never removed leaks a listener
  per conversation switch, each pinning its dead subtree forever. The shipped pattern is
  **listen only while open**: register when the thing opens, remove when it closes -
  a closed menu's handler shouldn't run at all.
- **`van.derive` outside a returned binding lives forever.** A derive created at
  `view` top level (not inside the tag tree you return) is registered against an
  always-connected sentinel and is never pruned - re-rendering the component stacks
  another immortal subscriber. Create derives **inside bindings that are part of the
  returned DOM**, or scope a top-level derive to the component root via `van.derive`'s
  third argument so van prunes it when the component leaves the DOM -
  `message-stream.js`'s scroll derives are the worked example:

  ```js
  // components/main/message-stream.js - scoped to `stream`, pruned on disconnect
  van.derive(
    () => { /* ... scroll work reading chat state ... */ },
    undefined,
    stream,
  );
  ```

  (Module-level derives in stores are fine - section 7.)
- **Races are settled by ownership, not hope.** Pass an `AbortController` signal into
  anything long-running and check ownership before touching shared state in `finally`
  (`activeController === ac` - section 9). For latest-wins loads, use a monotonic ticket:
  `conversations.open()` increments `openTicket` and discards stale responses.
- **Revoke what you mint** - blob URLs (section 11), timers tied to a component's lifetime.
  `http.sse()` is the model for streams: a `try/finally` cancels its reader on
  generator finalization, so a finished or abandoned turn never parks a connection.

---

## 13. Formatting - make it read like HTML

Tag calls are nested to mirror the DOM tree.

- **Props object** sits on the same line as the tag: `div({ class: "x" }, ...)`.
- **One child per line** when an element has more than one child or any nesting.
- **Trailing commas** on every child (clean diffs, easy reordering).
- The **closing `)`** lines up under the column of its opening tag.
- **Leaf elements** stay on one line: `span("test")`, `li(() => item.val)`.
- **Module-level constants** in SCREAMING_CASE, with a comment citing the server cap
  they mirror where one exists (`MAX_IMAGES`, `composer.js`).

Prefer:

```js
div({ class: "card" },
  h2("Title"),
  p("Some body text."),
  div({ class: "actions" },
    button({ onclick: save }, "Save"),
    button({ onclick: cancel }, "Cancel"),
  ),
)
```

Avoid cramming siblings onto one line:

```js
// no hard to scan, bad diffs
div({ class: "card" }, h2("Title"), p("Some body text."), button({ onclick: save }, "Save"))
```

---

## 14. Where files go

The directory layout, the four layers (`pages -> components -> state/services -> lib`),
and the no-npm pipeline are documented once, in
[ui-components.md](ui-components.md#2-directory-layout) - don't duplicate them here.
The one-line version: components in `components/<area>/kebab-case.js`, stores in
`state/`, all `/api` I/O in `services/`, vendored framework in `lib/`, global CSS in
`styles/`.

---

## Cheat sheet

- Component = `component({ name, css, view })` -> style / logic / content; file is
  `kebab-case.js`, export is `PascalCase`; imperative openers are camelCase verbs.
- Root element = **one root class, unique app-wide** (short form fine: `.tcard`, `.dd`);
  all component CSS namespaced under it.
- Colors (incl. shadows) = **tokens, always**; radii/rhythm (`--r*`, `--head-h`),
  type scale (`--fs-*`/`--lh-*`), and motion (`--t-fast`/`--t-slow`/`--ease`) = tokens;
  one-off component spacing may be literal (prefer `--sp-*`) - tokenize anything a
  breakpoint or theme would change.
- Theming = `light-dark()` tokens + `color-scheme`; `[data-theme]` is owned by
  `state/ui.js` alone.
- Responsiveness = `.app`-prefixed overrides + layout vars in `styles/layout.css`.
  No local `@media`.
- Lists = `vanX.reactive` rows + map-rebuild binding (the default); `vanX.list` is the
  opt-in for hot keyed lists; never alias reactive sub-fields.
- Routing = the one `mountRouter` call in `app.js`; settings is a modal, not a route.
- Services = `http.*` -> mutate store -> return; `pid()` helper; `ApiError`; no fetch
  outside `services/`.
- Stores = `van.state` scalars + `vanX.reactive` collections; reset-style setters;
  `addX` returns the reactive node; row shapes documented in a top comment.
- Errors = `attempt(fn, "Couldn't ...")` for user actions, bare `.catch` for background;
  nothing user-initiated fails silently; await when the next step depends on success.
- Streaming = WS -> SSE -> poll over one `seq` cursor; abort = detach = terminal;
  ownership checks in `finally`; components only ever bind to state.
- Overlays = imperative `Modal` (returns `close`) / `toast` / `openX`; snapshot
  primitives re-rendered by the caller's binding; z-ladder 30/50/60/70/80.
- Security = no HTML-string sinks; markdown via `lib/markdown.js` only; LLM output is
  hostile; sandboxed iframes without `allow-same-origin`; blob URLs revoked; token in
  memory only; CSP-clean styling by construction.
- Cleanup = document/window listeners live only while needed; component derives are
  scoped (inside a returned binding, or `van.derive`'s third arg); AbortController +
  ownership for races.
- Formatting = HTML-like; props inline, one child per line, trailing commas.
- I/O through `services/`, state through `state/`, never from a component directly.
