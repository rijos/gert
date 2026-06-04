// components/main/tool-chips.js — RAG / Search / Sandbox on-off chips.
// Reflects state/chat.tools; clicking toggles the per-conversation preference.
import van from "van";
import * as chat from "../../state/chat.js";

const { div, span } = van.tags;

const CHIPS = [
  { id: "rag", label: "RAG" },
  { id: "search", label: "Search" },
  { id: "sandbox", label: "Sandbox" },
];

const Chip = ({ id, label }) =>
  span(
    {
      class: () => "tool-chip" + (chat.tools[id] ? " on" : ""),
      onclick: () => chat.toggleTool(id),
      title: label + " tool",
    },
    span({ class: "dot" }),
    label,
  );

export const ToolChips = () =>
  div({ class: "tools" }, ...CHIPS.map((c) => Chip(c)));
