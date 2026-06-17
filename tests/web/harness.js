// harness.js - the component-unit harness bootstrap (testing.md section 8).
//
// This is an EXTERNAL module (served from tests/web/ at /tests/harness.js) rather
// than an inline <script> on purpose: the host's CSP is `script-src 'self'`, and
// imports resolve via absolute same-origin paths (no import map), so an inline
// bootstrap would be blocked and window.__mount would never define. Loading it as
// a same-origin module satisfies 'self' without weakening the production CSP.

// __mount(node) - append a freshly-built component node into a clean root and
// return it. Clears any previous mount so tests don't bleed into each other.
const root = document.getElementById("harness-root");
window.__mount = (node) => {
  root.replaceChildren(node);
  return node;
};
// Signal readiness for tests that wait on it.
window.__harnessReady = true;
