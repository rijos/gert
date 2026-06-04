// components/ui/seg-toggle.js — segmented toggle (.seg) e.g. Rendered/Source.
import van from "van";

const { div, button } = van.tags;

// options: [{ value, label }]; value: () => current; onSelect: (value) => void.
export const SegToggle = ({ options, value, onSelect } = {}) =>
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
  );
