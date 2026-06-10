# VanJS component style guide

How to **write** a component for the Gert SPA (`src/Gert.Api/wwwroot`). The guiding idea:
**every component is a pure function that bundles three concerns — style, content, and
logic — and all theming + responsiveness is driven by global CSS tokens.**

This is the *conventions* half of the front-end docs; the *map* half — where files live,
the four layers, the dev/release pipeline — is [ui-components.md](ui-components.md).

Dependencies: [VanJS](https://vanjs.org) and [VanX](https://vanjs.org/x) (reactive lists),
**vendored** in `lib/van.js` / `lib/van-x.js` and imported by the bare specifiers `van` /
`van-x` through the import map in `index.html` (no npm — [ui-components §6](ui-components.md#6-devrelease-pipeline-no-npm)).

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

// Adopt a stylesheet built from a CSS string. CSP: styles go through a
// Constructable Stylesheet (document.adoptedStyleSheets), NOT an inline <style>
// element — CSSOM construction is exempt from `style-src`, so a strict
// `style-src 'self'` holds with no 'unsafe-inline' / nonce / hash.
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
// components/sidebar/convo-item.js — one .convo row with its git-graph node.
import van from "van";
import { component } from "../../lib/component.js";
import * as chat from "../../state/chat.js";
import * as svc from "../../services/conversations.js";

const { div, span } = van.tags;

export const ConvoItem = component({
  name: "convo-item",
  css: `
    .convo{position:relative; padding:8px 10px; border-radius:var(--r-sm); cursor:pointer;
           display:flex; align-items:center; color:var(--ink-2); transition:.14s;}
    .convo:hover{background:var(--surface-2); color:var(--ink);}
    .convo.active{background:var(--chat-on); color:var(--coral-deep); font-weight:600;}
  `,
  view: (convo) => {
    // ── logic ───────────────────────────────────
    const open = () => svc.open(convo.id);   // service updates the store; binding re-renders

    // ── content ─────────────────────────────────
    return div(
      {
        class: () => "convo" + (chat.activeId.val === convo.id ? " active" : ""),
        onclick: open,
      },
      span({ class: "node" }),
      span({ class: "t" }, convo.title),
    );
  },
});
```

### Rules

- **One component per file.** Filename in `kebab-case.js` matching the factory `name`
  (`convo-item.js` → `name: "convo-item"`); the exported factory is **PascalCase**
  (`ConvoItem`), so call sites read like JSX: `ConvoItem(convo)`.
- **Named exports only** — no `default`. Keeps imports greppable and the import map flat.
- The **root element** gets `class: "<name>"` matching the factory `name`. All of the
  component's CSS is namespaced under that class.
- Keep `logic` (state + handlers) above `content`. A blank line and a comment
  marker between them is enough; don't split into separate functions unless the
  logic is genuinely reusable.
- Components take a **single argument** (props object) or a plain value when there's
  only one input. Provide defaults: `view: ({ start = 0 } = {}) => ...`.
- **No top-level side effects** — no `fetch`, no global mutation at import time. I/O goes
  through `services/`, state through `state/` ([ui-components §3](ui-components.md#3-the-four-layers)).

---

## 2. Theming — derive everything from global tokens

This is the heart of the guide. **Component CSS may never hardcode a color or a
spacing value. It may only reference `var(--token)`.** All real values live in
`styles/tokens.css`. This single rule gives you both themes *and* responsiveness
for free (see §3).

The two themes are **Manila** (paper / editorial light) and **Ember** (refined dark).
Every color token is defined **once** with `light-dark()`, and the document rides
`color-scheme`:

```css
/* styles/tokens.css — every color token defined exactly once */
:root{
  color-scheme: light dark;                       /* no explicit choice → follow the OS */
  --r:12px; --r-sm:8px;
  --bg:    light-dark(#f4ede1, #16110e);
  --ink:   light-dark(#3a2c20, #efe7df);          /* primary text */
  --ink-2: light-dark(#6f5d4c, #b3a79c);          /* secondary */
  --coral: light-dark(#dd5728, #ff6b3d);          /* the accent */
  /* … */
}

/* The toggle just PINS the scheme; light-dark() does the rest. */
[data-theme="manila"]{ color-scheme: light; }
[data-theme="ember"] { color-scheme: dark; }
```

Toggling the theme is owned by `state/ui.js`: it sets `documentElement[data-theme]`
and persists the choice to `localStorage` (a first-paint cache; the server-side
setting is the cross-device truth — [configuration §3.1](configuration.md#31-theme)).
Components never know which theme is active — they read tokens.

> Even *conditional rendering* by theme is done with tokens where possible: the
> sun/moon glyph swap in `theme-toggle.js` is driven by `--sun-display`/`--moon-display`
> tokens flipped in `tokens.css`, so the component itself stays theme-blind.

### What legitimately stays global

A component's `css` owns the rules namespaced under its own root class. Four kinds
of rule belong in the global stylesheets (`styles/*.css`) instead, because they aren't
owned by any one component:

- **Tokens** (`tokens.css`) — all color/spacing values + the theme scopes. Must load first.
- **Reset & document chrome** (`base.css`) — body + paper grain, scrollbars, keyframes.
- **App-frame layout + responsiveness** (`layout.css`) — the 3-column `.app` grid,
  collapse states, drawers, and **all `@media`** (see §3).
- **Shared utilities applied by bare class-string** (`primitives.css`) — e.g.
  `.btn`, `.ghost`, `.trash`, form/`.field` scaffolding. These are used across many
  components and have no single owner; a component using `class:"btn"` may render
  before any `Button` does, so the rule can't co-locate without a flash.

Everything else co-locates. When a parent styles into a child it composes
(`.model-picker.open .menu`), that rule lives with the **parent** — it owns that
relationship, and the higher specificity makes it order-independent of the child's
base rule.

---

## 3. Responsiveness — media queries are global, and only touch tokens

**Components never write `@media`.** All responsive behaviour lives in
`styles/layout.css` (plus the OS-fallback block in `tokens.css`), and it works by
*rewriting tokens / layout state at breakpoints*. Because components only read
tokens, they adapt automatically.

```css
/* styles/layout.css — the ONLY place media queries appear */
@media (max-width: 768px) {
  :root { --gap: 0.5rem; --pad: 1rem; }
  /* …drawer/scrim rules for the app frame… */
}
```

A grid component then needs zero responsive code of its own:

```js
export const Gallery = component({
  name: "gallery",
  css: `
    .gallery {
      display: grid;
      grid-template-columns: repeat(var(--cols), 1fr);
      gap: var(--gap);
    }
  `,
  view: (images) => div({ class: "gallery" }, /* ... */),
})
```

> If a component genuinely needs a layout shift that can't be expressed as a token
> change, that's a signal it should become two components selected by a global
> `breakpoint` state — not a local media query.

---

## 4. Lists & `foreach`

Use VanX for keyed, efficient list rendering. `vanX.list(container, items, itemFn)`
returns the container element; the item function receives the item state and a
`remove` deleter.

```js
import * as vanX from "van-x";

const items = vanX.reactive([])          // reactive array
items.push("new value")                  // mutating it updates the DOM

vanX.list(ul, items, (item, remove) =>
  li(
    span(() => item.val),                // read item.val inside a function for reactivity
    button({ onclick: remove }, "✕"),
  ),
)
```

Notes:
- The third argument's first param is a **state** — read it as `() => item.val`.
- **Do not alias** sub-fields of a reactive object into other variables; it breaks
  VanX's dependency tracking.
- For static (non-reactive) lists, plain `.map` is fine: `ul(items.map(i => li(i)))`.
- Which collections are VanX-reactive (conversations, docs, the message stream) vs
  plain `van.state` scalars is decided per store — see
  [ui-components §5](ui-components.md#5-cross-cutting-concerns).

---

## 5. Routing

We use a tiny dependency-free **History-API** router (`lib/router.js`): real paths
(not hashes), pattern matching with `:params`, and same-origin `<a data-link>`
interception so links navigate without a full page load. It renders the matched
handler's node into a host element.

```js
// navigate("/c/123") — programmatic navigation (history.pushState + re-resolve).
// <a href="/admin/users" data-link>Users</a> — intercepted; no full reload.
import { navigate, mountRouter } from "./lib/router.js"
```

Routing rules are declared in one place, when the router is mounted — the whole
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
> not a route — only chat and the admin user list are true pages.)

---

## 6. Formatting — make it read like HTML

Tag calls are nested to mirror the DOM tree.

- **Props object** sits on the same line as the tag: `div({ class: "x" }, ...)`.
- **One child per line** when an element has more than one child or any nesting.
- **Trailing commas** on every child (clean diffs, easy reordering).
- The **closing `)`** lines up under the column of its opening tag.
- **Leaf elements** stay on one line: `span("test")`, `li(() => item.val)`.

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
// ✗ hard to scan, bad diffs
div({ class: "card" }, h2("Title"), p("Some body text."), button({ onclick: save }, "Save"))
```

---

## 7. Where files go

The directory layout, the four layers (`pages → components → state/services → lib`),
and the no-npm pipeline are documented once, in
[ui-components.md](ui-components.md#2-directory-layout) — don't duplicate them here.
The one-line version: components in `components/<area>/kebab-case.js`, stores in
`state/`, all `/api` I/O in `services/`, vendored framework in `lib/`, global CSS in
`styles/`.

---

## Cheat sheet

- Component = `component({ name, css, view })` → style / logic / content; file is
  `kebab-case.js`, export is `PascalCase`.
- Component CSS references **only** `var(--token)` — never literal colors/spacing.
- Theming = `light-dark()` tokens + `color-scheme`; `[data-theme]` just pins the scheme.
- Responsiveness = token/layout rewrites inside `@media` in `styles/layout.css`. No local `@media`.
- Lists = `vanX.list(container, vanX.reactive([...]), (item, remove) => ...)`.
- Routing = the one `mountRouter` call in `app.js`; settings is a modal, not a route.
- Formatting = HTML-like; props inline, one child per line, trailing commas.
- I/O through `services/`, state through `state/`, never from a component directly.
