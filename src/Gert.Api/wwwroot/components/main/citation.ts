// components/main/citation.js - inline [n] superscript marker. Built by the
// message body's citation injection (one chip per matched [n]) and exported for
// the component-unit harness.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import type { Citation as CitationRow } from "../../state/chat.js";

const { span } = van.tags;

// ordinal/label are the matching Citation row fields; both optional so the
// no-arg default `{}` (the harness) stays valid - String(undefined) is "undefined".
export const Citation = component({
  name: "citation",
  css: `
    .cite {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      vertical-align: super;
      color: var(--coral-deep);
      background: var(--surface-2);
      border: 1px solid var(--line);
      border-radius: 5px;
      padding: 1px 5px;
      margin: 0 2px;
      cursor: pointer;
      line-height: 1;
      transition: var(--t-fast);
    }
    .cite:hover {
      background: var(--coral-soft);
      border-color: var(--coral);
    }
  `,
  view: ({ ordinal, label }: Partial<Pick<CitationRow, "ordinal" | "label">> = {}) =>
    span({ class: "cite", title: label || "" }, String(ordinal)),
});
