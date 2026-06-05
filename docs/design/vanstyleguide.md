# VanJS Component Style Guide

A convention for building clean, consistent VanJS components. The guiding idea:
**every component is a pure function that bundles three concerns — style, content, and
logic — and all theming + responsiveness is driven by global CSS tokens.**

Dependencies: [`vanjs-core`](https://vanjs.org) and [`vanjs-ext`](https://vanjs.org/x) (VanX, for reactive lists).

---

## 1. The component shape

Components are just functions, but we give them a consistent shape with a small
`component()` factory. It maps directly onto the three concerns:

| Concern   | Where it lives                          |
|-----------|------------------------------------------|
| `style`   | the `css` string (injected once)         |
| `logic`   | everything before `return` in `view`     |
| `content` | the tag tree you `return`                |

```js
// lib/component.js
import van from "vanjs-core"

const injected = new Set()

// Bundles style + content + logic. CSS is injected into <head>
// exactly once per component name, the first time it renders.
export const component = ({ name, css, view }) => (...args) => {
  if (css && !injected.has(name)) {
    injected.add(name)
    van.add(document.head, van.tags.style(css))
  }
  return view(...args)
}
```

### Authoring a component

```js
// components/TodoList.js
import van from "vanjs-core"
import * as vanX from "vanjs-ext"
import { component } from "../lib/component.js"

const { div, ul, li, input, button, span } = van.tags

export const TodoList = component({
  name: "todo-list",
  css: `
    .todo-list { display: flex; flex-direction: column; gap: var(--gap); }
    .todo-list ul { list-style: none; margin: 0; padding: 0; }
    .todo-list li {
      display: flex;
      justify-content: space-between;
      padding: var(--pad-sm);
      color: var(--fg);
      border-bottom: 1px solid var(--border);
    }
    .todo-list button { color: var(--accent); }
  `,
  view: (initial = []) => {
    // ── logic ───────────────────────────────────
    const items = vanX.reactive(initial)
    const draft = van.state("")
    const add = () => {
      if (!draft.val.trim()) return
      items.push(draft.val.trim())
      draft.val = ""
    }

    // ── content ─────────────────────────────────
    return div({ class: "todo-list" },
      input({
        type: "text",
        value: draft,
        oninput: e => draft.val = e.target.value,
      }),
      button({ onclick: add }, "Add"),
      vanX.list(ul, items, (item, remove) =>
        li(
          span(() => item.val),
          button({ onclick: remove }, "✕"),
        ),
      ),
    )
  },
})
```

### Rules

- **One component per file.** Filename = component name in PascalCase (`TodoList.js`).
- **PascalCase** for the exported component, so call sites read like JSX: `TodoList()`.
- The **root element** gets `class: "<name>"` matching the factory `name`
  (kebab-case). All of the component's CSS is namespaced under that class.
- Keep `logic` (state + handlers) above `content`. A blank line and a comment
  marker between them is enough; don't split into separate functions unless the
  logic is genuinely reusable.
- Components take a **single argument** (props object) or a plain value when there's
  only one input. Provide defaults: `view: ({ start = 0 } = {}) => ...`.

---

## 2. Theming — derive everything from global tokens

This is the heart of the guide. **Component CSS may never hardcode a color or a
spacing value. It may only reference `var(--token)`.** All real values live in
`global.css`. This single rule gives you dark mode *and* responsiveness for free
(see §3).

```css
/* global.css */
:root {
  /* color tokens */
  --bg:        #ffffff;
  --fg:        #1a1a1a;
  --accent:    #3b82f6;
  --on-accent: #ffffff;
  --border:    #e5e5e5;

  /* layout / spacing tokens */
  --gap:       1rem;
  --pad:       1.5rem;
  --pad-sm:    0.5rem;
  --cols:      3;
  --max-width: 1100px;
}

/* Dark mode = reassign the same tokens. Components don't change. */
[data-theme="dark"] {
  --bg:        #121212;
  --fg:        #f1f1f1;
  --accent:    #60a5fa;
  --on-accent: #0b0b0b;
  --border:    #2a2a2a;
}

body {
  background: var(--bg);
  color: var(--fg);
  font-family: system-ui, sans-serif;
}
```

Toggling theme is one line:

```js
export const toggleTheme = () => {
  const el = document.documentElement
  el.dataset.theme = el.dataset.theme === "dark" ? "" : "dark"
}
```

---

## 3. Responsiveness — media queries are global, and only touch tokens

**Components never write `@media`.** All responsive behaviour lives in `global.css`,
and it works by *rewriting the tokens at breakpoints*. Because components only read
tokens, they adapt automatically.

```css
/* global.css — the ONLY place media queries appear */
@media (max-width: 768px) {
  :root {
    --gap:  0.5rem;
    --pad:  1rem;
    --cols: 1;
  }
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
import * as vanX from "vanjs-ext"

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

---

## 5. Routing

VanJS is unopinionated about routing, so we keep a tiny dependency-free router: a
`route` state driven by `hashchange`, a routes map, and a `Router` component.

```js
// lib/router.js
import van from "vanjs-core"
const { a, div } = van.tags

const current = () => location.hash.slice(1) || "/"

export const route = van.state(current())
window.addEventListener("hashchange", () => route.val = current())

// <Link href="/about">About</Link>
export const Link = (href, ...children) => a({ href: `#${href}` }, ...children)

// Renders the component matching the current route.
export const Router = (routes, NotFound = () => div("404")) =>
  div(() => (routes[route.val] ?? NotFound)())
```

Routing rules are a plain object — one place to see the whole app map:

```js
// routes.js
import { Home }  from "./components/Home.js"
import { About } from "./components/About.js"

export const routes = {
  "/":      Home,
  "/about": About,
}
```

```js
// app.js
import van from "vanjs-core"
import { Router } from "./lib/router.js"
import { routes } from "./routes.js"

van.add(document.body, Router(routes))
```

> Need params (`/user/:id`) or guards? Either extend the matcher in `Router`, or
> adopt the community [`vanjs-router`](https://github.com/iuroc/vanjs-router) add-on.
> Keep routing rules declarative and in one file regardless.

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

## 7. Suggested project layout

```
src/
  global.css          # tokens, dark mode, ALL media queries
  app.js              # mounts the Router
  routes.js           # the route → component map
  lib/
    component.js      # the component() factory
    router.js         # route state + Router + Link
    theme.js          # toggleTheme()
  components/
    TodoList.js       # one component per file
    Gallery.js
    Home.js
    About.js
```

---

## Cheat sheet

- Component = `component({ name, css, view })` → style / logic / content.
- Component CSS references **only** `var(--token)` — never literal colors/spacing.
- Dark mode = reassign tokens under `[data-theme="dark"]`.
- Responsiveness = reassign tokens inside `@media` in `global.css`. No local `@media`.
- Lists = `vanX.list(container, vanX.reactive([...]), (item, remove) => ...)`.
- Routing = a `routes` map + `Router` + `Link`, driven by a `route` state.
- Formatting = HTML-like; props inline, one child per line, trailing commas.