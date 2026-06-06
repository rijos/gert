// components/ui/seg-toggle.js — segmented toggle (.seg) e.g. Rendered/Source.
import van from "van";
import { component } from "../../lib/component.js";

const { div, button } = van.tags;

export const SegToggle = component({
  name: "seg-toggle",
  css: `
    .seg{display:flex; background:var(--surface-2); border:1px solid var(--line); border-radius:7px; padding:2px; flex:none;}
    .sgb{font-family:var(--sans); font-size:11px; font-weight:600; color:var(--ink-2); border:none; background:none; padding:4px 9px; border-radius:5px; cursor:pointer; transition:.12s;}
    /* active segment: coral treatment (--accent-deep keeps small text AA on cream) */
    .sgb.on{background:var(--coral-soft); color:var(--coral-deep);}
  `,
  // options: [{ value, label }]; value: () => current; onSelect: (value) => void.
  view: ({ options, value, onSelect } = {}) =>
    div(
      { class: "seg" },
      ...options.map((o) =>
        button(
          {
            class: () => "sgb" + (value() === o.value ? " on" : ""),
            onclick: () => onSelect(o.value),
          },
          o.label,
        ),
      ),
    ),
});
