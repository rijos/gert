// router.js - minimal History-API router (~40 lines).
// Routes: "/" and "/c/:id" -> chat, "/admin/users". (Settings is a modal,
// not a route - spa-style-guide section 5.)
// A route's handler receives matched params and returns a VanJS DOM node;
// mountRouter renders it into the host element (the main region).

type RouteParams = Record<string, string>;
type RouteHandler = (params: RouteParams) => unknown;
interface Route {
  rx: RegExp;
  names: string[];
  handler: RouteHandler;
}

const routes: Route[] = [];
let host: Element | null = null;
let render: ((node: unknown) => void) | null = null;

const define = (pattern: string, handler: RouteHandler) => {
  const names: string[] = [];
  const rx = new RegExp(
    "^" +
      pattern.replace(/:[^/]+/g, (m) => {
        names.push(m.slice(1));
        return "([^/]+)";
      }) +
      "/?$",
  );
  routes.push({ rx, names, handler });
};

const match = (path: string) => {
  for (const r of routes) {
    const m = r.rx.exec(path);
    if (!m) continue;
    const params: RouteParams = {};
    // m[i+1] is the capture for the i-th param name; the regex was built with one
    // capture group per name, so the group always matched when m is non-null (?? ""
    // preserves the prior behavior of feeding a defined value to decodeURIComponent).
    r.names.forEach((n, i) => (params[n] = decodeURIComponent(m[i + 1] ?? "")));
    return { handler: r.handler, params };
  }
  return null;
};

const resolve = () => {
  const path = location.pathname || "/";
  const hit = match(path) || match("/");
  if (hit && host && render) render(hit.handler(hit.params));
};

// Navigate without a full page load.
export const navigate = (path: string) => {
  if (path === location.pathname) return;
  history.pushState({}, "", path);
  resolve();
};

// Intercept same-origin <a data-link> clicks so links use the router.
// Only app-internal paths qualify: a single leading "/" - NOT "//host"
// (protocol-relative = external), and not absolute URLs, mailto:/tel:/any
// other scheme, or fragment/relative hrefs. Those fall through to the browser.
const isInternal = (href: string) => href.startsWith("/") && !href.startsWith("//");

const onClick = (e: Event) => {
  // Narrow the event target to Element for .closest (annotation-only; the original
  // `e.target.closest &&` guard already handled non-Element targets).
  const target = e.target as Element | null;
  const a = target && target.closest && target.closest("a[data-link]");
  if (!a) return;
  const href = a.getAttribute("href");
  if (!href || !isInternal(href)) return;
  e.preventDefault();
  navigate(href);
};

// mountRouter({ host, render, define: fn }) - define routes, then start.
export const mountRouter = (
  { host: h, render: r, routes: declare }: {
    host: Element;
    render: (node: unknown) => void;
    routes: (define: (pattern: string, handler: RouteHandler) => void) => void;
  },
) => {
  host = h;
  render = r;
  declare(define);
  window.addEventListener("popstate", resolve);
  document.addEventListener("click", onClick);
  resolve();
};
