// components/ui/seg-toggle.js - segmented toggle (.seg) e.g. Rendered/Source.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";

const { div, button } = van.tags;

interface SegOption {
  value: string;
  label: string;
}

interface SegToggleProps {
  options: SegOption[];
  value: () => string;
  onSelect: (value: string) => void;
}

export const SegToggle = component({
  name: "seg-toggle",
  css: `
    .seg {
      display: flex;
      background: var(--surface-2);
      border: 1px solid var(--line);
      border-radius: 7px;
      padding: 2px;
      flex: none;
    }

    .sgb {
      font-family: var(--sans);
      font-size: var(--fs-xs);
      font-weight: 600;
      color: var(--ink-2);
      border: none;
      background: none;
      padding: 4px 9px;
      border-radius: 5px;
      cursor: pointer;
      transition: var(--t-fast);
    }

    /* active segment: filled accent - the selected mode reads at a glance
       (--coral-deep/--on-accent is the AA pairing from primitives' .btn) */
    .sgb.on {
      background: var(--coral-deep);
      color: var(--on-accent);
    }
  `,
  // options: [{ value, label }]; value: () => current; onSelect: (value) => void.
  // `= {} as SegToggleProps` keeps the no-arg default while typing the
  // always-passed fields as required (so `options.map` / `value()` type-check).
  view: ({ options, value, onSelect }: SegToggleProps = {} as SegToggleProps) =>
    div(
      { class: "seg" },
      ...options.map((o) =>
        button({ class: () => "sgb" + (value() === o.value ? " on" : ""), onclick: () => onSelect(o.value) },
          o.label,
        ),
      ),
    ),
});
