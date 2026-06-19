// The component() factory: style / logic / content in one function, each in its own slot.
//   css   : stylesheet, adopted once per `name` on first render.
//   setup : (OPTIONAL) logic - `van.state`, handlers, derived plain values - as a typed bag.
//           Runs once per render, before `view`.
//   view  : the tag tree. Gets the setup bag first (when `setup` is present), then the call args.
//
// LIFETIME (spa-style-guide section 12): `setup` may hold `van.state` + handlers, but a
// `van.derive` that must be pruned with the component MUST be created INSIDE a binding in
// `view` (or scoped via van.derive's 3rd arg) - never in `setup`, where it would leak.
//
// CSP: styles are adopted via a Constructable Stylesheet (document.adoptedStyleSheets), NOT an
// inline <style> element. CSSOM stylesheet construction is exempt from the `style-src` directive,
// so this satisfies a strict `style-src 'self'` policy with no 'unsafe-inline' / nonce / hash.
// (Inline `style:` props set by VanJS go through the CSSOM too - el.style - so they're likewise
// CSP-clean.) Adopted sheets cascade AFTER the <link>ed globals, so layout.css keeps priority for
// responsive overrides. Genuinely global rules (tokens, reset, .app grid, @media) stay in styles/*.css.

const injected = new Set<string>();

// Conservative minifier for THIS project's own component stylesheets (not a general-purpose
// CSS minifier): drop comments, collapse whitespace/newlines, and tighten around block and
// declaration punctuation. It deliberately does NOT touch `,`/`:` (they appear inside selectors
// and content:"" values), so it is safe to run blind over every component css block. The
// release bundler runs the same shape at build time so the shipped app.js carries no verbose CSS.
export const minifyCss = (src: string): string =>
  src
    .replace(/\/\*[\s\S]*?\*\//g, "") // drop comments, including /*! legal */ banners
    .replace(/\s+/g, " ") // collapse whitespace + newlines to a single space
    .replace(/\s*([{};])\s*/g, "$1") // tighten around braces + statement separators
    .replace(/;}/g, "}") // a block's trailing semicolon is redundant
    .trim();

// css tagged template: authors a minified component stylesheet inline. Interpolations are
// stringified, then the whole block is minified. The "small component that minifies css" -
// adoptStyles also minifies as a backstop, so a plain css string works too.
export const css = (strings: TemplateStringsArray, ...values: unknown[]): string =>
  minifyCss(strings.reduce((acc, s, i) => acc + s + (i < values.length ? String(values[i]) : ""), ""));

// Adopt a stylesheet built from a CSS string. Reusable by non-component modules that need to ship
// CSS under a strict CSP (e.g. the imperative toast host). Minifies on the way in so injected
// component CSS carries no comments/whitespace regardless of how the source was authored.
export const adoptStyles = (style: string) => {
  const sheet = new CSSStyleSheet();
  sheet.replaceSync(minifyCss(style));
  document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
};

// A component with NO logic slot: `view` takes the call args directly. `setup?: never` keeps this
// overload DISJOINT from the setup form below - a spec that carries `setup` can never match here, so
// any TS checker resolves the setup overload unambiguously (tsgo and tsc agree). Without it, a
// `view: (state, arg) => R` is structurally compatible with `view: (...args) => R`, so this overload
// would match a setup component too and reject `setup` as an excess property.
interface ViewSpec<Args extends unknown[], R> {
  name: string;
  css?: string;
  setup?: never;
  view: (...args: Args) => R;
}

// A component WITH a logic slot: `setup` builds the typed state bag from the call args, and `view`
// receives that bag first, then the same call args.
interface SetupSpec<Args extends unknown[], S, R> {
  name: string;
  css?: string;
  setup: (...args: Args) => S;
  view: (state: S, ...args: Args) => R;
}

// Setup form declared first so it is preferred when `setup` is present (the `setup?: never` above
// already makes the two disjoint; the order is belt-and-braces).
export function component<Args extends unknown[], S, R>(spec: SetupSpec<Args, S, R>): (...args: Args) => R;
export function component<Args extends unknown[], R>(spec: ViewSpec<Args, R>): (...args: Args) => R;
export function component<Args extends unknown[], S, R>(
  spec: SetupSpec<Args, S, R> | ViewSpec<Args, R>,
): (...args: Args) => R {
  return (...args: Args): R => {
    if (spec.css && !injected.has(spec.name)) {
      injected.add(spec.name);
      adoptStyles(spec.css);
    }

    // Only the setup form has a (truthy) `setup`; that narrows the union to SetupSpec here.
    return spec.setup ? spec.view(spec.setup(...args), ...args) : spec.view(...args);
  };
}
