// components/main/tool-chips.js — RAG / Search / Sandbox on-off chips.
// Reflects state/chat.tools; clicking toggles the per-conversation preference.
// Inert when the selected model can't call tools (the server drops them anyway
// — IModelCatalog gates the turn planner; this mirrors that in the UI).
import van from "van";
import { component } from "../../lib/component.js";
import * as chat from "../../state/chat.js";
import * as models from "../../state/models.js";

const { div, span } = van.tags;

const CHIPS = [
  { id: "rag", label: "RAG" },
  { id: "search", label: "Search" },
  { id: "sandbox", label: "Sandbox" },
];

const Chip = ({ id, label }) =>
  span(
    {
      class: () =>
        "tool-chip" +
        (chat.tools[id] ? " on" : "") +
        (models.selectedSupportsTools.val ? "" : " disabled"),
      onclick: () => models.selectedSupportsTools.val && chat.toggleTool(id),
      title: () =>
        models.selectedSupportsTools.val
          ? label + " tool"
          : "This model doesn't support tool calling",
    },
    span({ class: "dot" }),
    label,
  );

export const ToolChips = component({
  name: "tool-chips",
  css: `
    .tools{display:flex; gap:6px; align-items:center;}
    .tool-chip{font-family:var(--mono); font-size:10.5px; letter-spacing:.01em; padding:4px 9px; border-radius:20px; border:1px solid var(--line-strong); color:var(--ink-faint); background:var(--surface); display:flex; align-items:center; gap:5px; cursor:pointer;}
    .tool-chip .dot{width:6px; height:6px; border-radius:50%; background:var(--ink-faint);}
    .tool-chip.on{color:var(--sage); border-color:var(--sage-soft); background:var(--sage-soft);}
    .tool-chip.on .dot{background:var(--sage);}
    .tool-chip.disabled{opacity:.4; cursor:not-allowed;}
  `,
  view: () => div({ class: "tools" }, ...CHIPS.map((c) => Chip(c))),
});
