// icons/icons.js — named SVG factories (de-dupes the mockup's inline SVGs).
// Usage: Icon("trash", { size: 14, class: "fi" }). Returns a VanJS <svg> node.
import van from "van";

// van.tags is a Proxy: calling it with a namespace URI yields namespaced tags.
const { svg, path, circle, rect } = van.tags("http://www.w3.org/2000/svg");

// each entry: (s) => array of child nodes for a 0 0 24 24 viewBox
const GLYPHS = {
  // brand mark (custom viewBox handled separately)
  plus: () => [path({ d: "M12 5v14M5 12h14", "stroke-linecap": "round" })],
  close: () => [path({ d: "M18 6 6 18M6 6l12 12", "stroke-linecap": "round" })],
  search: () => [
    path({ d: "M21 21l-4.3-4.3M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14z" }),
  ],
  file: () => [
    path({ d: "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" }),
    path({ d: "M14 2v6h6" }),
  ],
  trash: () => [
    path({
      d: "M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m2 0v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6",
    }),
  ],
  lock: () => [
    rect({ x: "5", y: "11", width: "14", height: "9", rx: "2" }),
    path({ d: "M8 11V8a4 4 0 0 1 8 0v3" }),
  ],
  gear: () => [
    circle({ cx: "12", cy: "12", r: "3" }),
    path({
      d: "M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 8 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H2a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 3.6 8a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H8a1.65 1.65 0 0 0 1-1.51V2a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V8a1.65 1.65 0 0 0 1.51 1H22a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z",
    }),
  ],
  // two-lobe brain — the thinking/reasoning affordance
  brain: () => [
    path({
      d: "M12 4.5a2.5 2.5 0 0 0-4.96-.46 2.5 2.5 0 0 0-1.98 3 2.5 2.5 0 0 0-1.32 4.24 2.5 2.5 0 0 0 1.98 3A2.5 2.5 0 0 0 9.5 16.5c.97 0 1.85-.55 2.28-1.35",
    }),
    path({
      d: "M12 4.5a2.5 2.5 0 0 1 4.96-.46 2.5 2.5 0 0 1 1.98 3 2.5 2.5 0 0 1 1.32 4.24 2.5 2.5 0 0 1-1.98 3 2.5 2.5 0 0 1-3.78 2.92",
    }),
    path({ d: "M12 4.5v15" }),
  ],
  sidebar: () => [
    rect({ x: "3", y: "4", width: "18", height: "16", rx: "2" }),
    path({ d: "M9 4v16" }),
  ],
  panel: () => [
    rect({ x: "3", y: "4", width: "18", height: "16", rx: "2" }),
    path({ d: "M15 4v16" }),
  ],
  edit: () => [
    path({ d: "M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" }),
    path({ d: "M18.5 2.5a2.12 2.12 0 0 1 3 3L12 15l-4 1 1-4z" }),
  ],
  globe: () => [
    circle({ cx: "12", cy: "12", r: "10" }),
    path({ d: "M2 12h20M12 2a15 15 0 0 1 0 20 15 15 0 0 1 0-20z" }),
  ],
  moon: () => [
    path({ d: "M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z" }),
  ],
  sun: () => [
    circle({ cx: "12", cy: "12", r: "4.2" }),
    path({
      d: "M12 2v2.5M12 19.5V22M4.9 4.9l1.8 1.8M17.3 17.3l1.8 1.8M2 12h2.5M19.5 12H22M4.9 19.1l1.8-1.8M17.3 6.7l1.8-1.8",
      "stroke-linecap": "round",
    }),
  ],
  chevron: () => [
    path({ d: "M6 9l6 6 6-6", "stroke-linecap": "round" }),
  ],
  send: () => [
    path({ d: "M22 2 11 13M22 2l-7 20-4-9-9-4z", "stroke-linejoin": "round" }),
  ],
  // filled rounded square — the universal "stop" affordance.
  stop: () => [
    rect({ x: "6", y: "6", width: "12", height: "12", rx: "2.5", fill: "currentColor" }),
  ],
  attach: () => [
    path({
      d: "M21.4 11.05 12.25 20.2a5.5 5.5 0 0 1-7.78-7.78l9.2-9.2a3.5 3.5 0 0 1 4.95 4.95l-9.2 9.19a1.5 1.5 0 0 1-2.12-2.12l8.49-8.48",
    }),
  ],
  book: () => [
    path({ d: "M4 19.5A2.5 2.5 0 0 1 6.5 17H20" }),
    path({ d: "M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z" }),
  ],
  expand: () => [
    path({
      d: "M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7",
      "stroke-linecap": "round",
      "stroke-linejoin": "round",
    }),
  ],
  upload: () => [
    path({
      d: "M12 16V4M7 9l5-5 5 5",
      "stroke-linecap": "round",
      "stroke-linejoin": "round",
    }),
    path({ d: "M4 16v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2" }),
  ],
  download: () => [
    path({
      d: "M12 4v12M7 11l5 5 5-5",
      "stroke-linecap": "round",
      "stroke-linejoin": "round",
    }),
    path({ d: "M4 16v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2" }),
  ],
  user: () => [
    circle({ cx: "12", cy: "8", r: "4" }),
    path({ d: "M4 21a8 8 0 0 1 16 0" }),
  ],
  copy: () => [
    rect({ x: "9", y: "9", width: "13", height: "13", rx: "2" }),
    path({ d: "M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" }),
  ],
  check: () => [
    path({ d: "M20 6 9 17l-5-5", "stroke-linecap": "round", "stroke-linejoin": "round" }),
  ],
  external: () => [
    path({ d: "M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" }),
    path({ d: "M15 3h6v6M10 14 21 3", "stroke-linecap": "round", "stroke-linejoin": "round" }),
  ],
  clock: () => [
    circle({ cx: "12", cy: "12", r: "10" }),
    path({ d: "M12 6v6l4 2", "stroke-linecap": "round", "stroke-linejoin": "round" }),
  ],
  // checklist — the todo tool card mark
  checklist: () => [
    path({ d: "M3.5 5.5 5 7l2.5-2.5M3.5 11.5 5 13l2.5-2.5M3.5 17.5 5 19l2.5-2.5", "stroke-linecap": "round", "stroke-linejoin": "round" }),
    path({ d: "M11 6h10M11 12h10M11 18h10", "stroke-linecap": "round" }),
  ],
  // globe + magnifier — the web-search/sources mark
  websearch: () => [
    circle({ cx: "10.5", cy: "10.5", r: "7.5" }),
    path({ d: "M3 10.5h15M10.5 3a11.4 11.4 0 0 1 0 15 11.4 11.4 0 0 1 0-15z" }),
    circle({ cx: "17.5", cy: "17.5", r: "3.2" }),
    path({ d: "M19.9 19.9 22 22", "stroke-linecap": "round" }),
  ],
};

export const Icon = (name, opts = {}) => {
  const size = opts.size || 16;
  const sw = opts.strokeWidth || 2;
  const children = (GLYPHS[name] || GLYPHS.file)();
  return svg(
    {
      viewBox: "0 0 24 24",
      width: size,
      height: size,
      fill: "none",
      stroke: "currentColor",
      "stroke-width": sw,
      class: opts.class || "",
    },
    ...children,
  );
};

// brand mark — its own viewBox, used once. Color rides the --brand token
// (tokens.css; never themed) via the style: prop — SVG presentation
// attributes can't resolve var(), inline CSSOM can.
export const BrandMark = () =>
  svg(
    { width: 30, height: 30, viewBox: "0 0 30 30", fill: "none" },
    circle({ cx: "8", cy: "7", r: "3.4", style: "fill:var(--brand)" }),
    circle({ cx: "8", cy: "23", r: "3.4", style: "fill:var(--brand)" }),
    circle({
      cx: "22",
      cy: "15",
      r: "3.4",
      style: "stroke:var(--brand)",
      "stroke-width": "2",
      fill: "none",
    }),
    path({
      d: "M8 10.4v9.2M8 12 Q8 15 12 15 L18.6 15M8 18 Q8 15 12 15",
      style: "stroke:var(--brand)",
      "stroke-width": "2",
      fill: "none",
      "stroke-linecap": "round",
    }),
  );

// theme toggle pair (CSS shows the right one per theme)
export const ThemeGlyphs = () => [
  Icon("moon", { class: "moon" }),
  Icon("sun", { class: "sun" }),
];
