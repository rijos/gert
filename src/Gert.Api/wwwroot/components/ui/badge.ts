import van from "/lib/van.js";
import { component } from "../../lib/component.js";

const { span } = van.tags;

export const Badge = component({
  name: "badge",
  css: `
    .badge {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      padding: 2px 6px;
      border-radius: 5px;
      background: var(--surface-2);
      color: var(--ink-2);
      border: 1px solid var(--line);
    }

    .badge.cap {
      color: var(--coral-deep);
      background: var(--coral-soft);
      border-color: transparent;
    }
  `,
  // cap: accent capability badge; otherwise neutral meta badge.
  view: ({ label, cap = false }: { label?: string; cap?: boolean } = {}) =>
    span({ class: "badge" + (cap ? " cap" : "") }, label),
});
