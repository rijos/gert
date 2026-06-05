// components/main/tool-chips.js — RAG / Search / Sandbox on-off chips.
// Reflects state/chat.tools; clicking toggles the per-conversation preference.
// Inert when the selected model can't call tools (the server drops them anyway
// — IModelCatalog gates the turn planner; this mirrors that in the UI).
import van from "van";
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

export const ToolChips = () =>
  div({ class: "tools" }, ...CHIPS.map((c) => Chip(c)));
