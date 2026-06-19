// lib/component.ts - the component() factory.
// Bundles a component's three concerns - style / logic / content - into one function,
// each in its own slot:
//   css    : the component's stylesheet, adopted into the document exactly once per `name`,
//            the first time the component renders.
//   setup  : (OPTIONAL) the logic - reactive `van.state`, handlers, derived plain values -
//            returned as a typed bag. Runs once per render, before `view`.
//   view   : the content - the tag tree. Gets the setup bag as its first arg (when `setup`
//            is present), then the component's own call args.
// A leaf with no logic omits `setup`; `view` then takes the call args directly.
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

// Adopt a stylesheet built from a CSS string. Reusable by non-component modules that need to ship
// CSS under a strict CSP (e.g. the imperative toast host).
export const adoptStyles = (css: string) => {
  const sheet = new CSSStyleSheet();
  sheet.replaceSync(css);
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
