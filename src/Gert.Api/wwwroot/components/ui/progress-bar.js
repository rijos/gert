// components/ui/progress-bar.js — the shared horizontal progress bar: a track +
// a width-percent fill, with the ARIA progressbar contract stamped on. Renders a
// SNAPSHOT of {value, max} — callers re-render it from their own reactive
// bindings (the house pattern), so the component itself stays binding-free.
// Geometry (height/radius/margins) is the call site's: pass a modifier class and
// scope overrides as `.pbar.your-class` (e.g. tool-card's .tprog, the context
// popover's .cx-bar). `color` overrides the fill (inline style beats the sheet).
import van from "van";
import { component } from "../../lib/component.js";

const { div, i } = van.tags;

export const ProgressBar = component({
  name: "progress-bar",
  css: `
    .pbar{height:4px; background:var(--surface-2); overflow:hidden;}
    .pbar > i{display:block; height:100%; background:var(--coral); transition:width var(--t-slow) var(--ease), background var(--t-slow) var(--ease);}
  `,
  view: ({ value = 0, max = 1, color = "", class: cls = "" } = {}) => {
    const pct = max > 0 ? Math.min(Math.max(value / max, 0), 1) * 100 : 0;
    return div(
      {
        class: "pbar" + (cls ? " " + cls : ""),
        role: "progressbar",
        "aria-valuemin": "0",
        "aria-valuemax": String(max),
        "aria-valuenow": String(value),
      },
      i({ style: `width:${pct.toFixed(1)}%;` + (color ? ` background:${color};` : "") }),
    );
  },
});
