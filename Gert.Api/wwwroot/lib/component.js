// lib/component.js — the component() factory.
// Bundles a component's three concerns — style / logic / content — into one
// function. The `css` string is adopted into the document exactly once per `name`,
// the first time the component renders.
//
// CSP: styles are adopted via a Constructable Stylesheet (document.adoptedStyleSheets),
// NOT an inline <style> element. CSSOM stylesheet construction is exempt from the
// `style-src` directive, so this satisfies a strict `style-src 'self'` policy with
// no 'unsafe-inline' / nonce / hash. (Inline `style:` props set by VanJS go through
// the CSSOM too — el.style — so they're likewise CSP-clean.) Adopted sheets cascade
// AFTER the <link>ed globals, so layout.css keeps priority for responsive overrides.
//
// Genuinely global rules (tokens, reset, .app grid, @media) stay in styles/*.css.

const injected = new Set();

// Adopt a stylesheet built from a CSS string. Reusable by non-component modules
// that need to ship CSS under a strict CSP (e.g. the imperative toast host).
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
