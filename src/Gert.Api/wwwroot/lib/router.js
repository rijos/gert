// router.js - minimal History-API router (~40 lines).
// Routes: "/" and "/c/:id" -> chat, "/admin/users". (Settings is a modal,
// not a route - spa-style-guide section 5.)
// A route's handler receives matched params and returns a VanJS DOM node;
// mountRouter renders it into the host element (the main region).

const routes = [];
let host = null;
let render = null;

const define = (pattern, handler) => {
  const names = [];
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

const match = (path) => {
  for (const r of routes) {
    const m = r.rx.exec(path);
    if (!m) continue;
    const params = {};
    r.names.forEach((n, i) => (params[n] = decodeURIComponent(m[i + 1])));
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
export const navigate = (path) => {
  if (path === location.pathname) return;
  history.pushState({}, "", path);
  resolve();
};

// Intercept same-origin <a data-link> clicks so links use the router.
// Only app-internal paths qualify: a single leading "/" - NOT "//host"
// (protocol-relative = external), and not absolute URLs, mailto:/tel:/any
// other scheme, or fragment/relative hrefs. Those fall through to the browser.
const isInternal = (href) => href.startsWith("/") && !href.startsWith("//");

const onClick = (e) => {
  const a = e.target.closest && e.target.closest("a[data-link]");
  if (!a) return;
  const href = a.getAttribute("href");
  if (!href || !isInternal(href)) return;
  e.preventDefault();
  navigate(href);
};

// mountRouter({ host, render, define: fn }) - define routes, then start.
export const mountRouter = ({ host: h, render: r, routes: declare }) => {
  host = h;
  render = r;
  declare(define);
  window.addEventListener("popstate", resolve);
  document.addEventListener("click", onClick);
  resolve();
};
