// components/main/tools-menu.js — composer dropdown of tool toggles (RAG /
// Search / Sandbox / Todos / Clock) plus "Use my docs". Replaces the top-bar
// chips to keep the top bar quiet. Reflects state/chat.tools and
// state/knowledge.useInChat; rows toggle in place (the menu stays open).
// Tool rows go inert when the selected model can't call tools (the server
// drops them anyway — IModelCatalog gates the turn planner; this mirrors it).
import van from "van";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { Menu } from "../ui/menu.js";
import { Switch } from "../ui/switch.js";
import * as chat from "../../state/chat.js";
import * as models from "../../state/models.js";
import * as knowledge from "../../state/knowledge.js";

const { div, button, span } = van.tags;

const TOOLS = [
  { id: "rag", label: "RAG" },
  { id: "search", label: "Search" },
  { id: "sandbox", label: "Sandbox" },
  { id: "todo", label: "Todos" },
  { id: "clock", label: "Clock" },
];

export const ToolsMenu = component({
  name: "tools-menu",
  css: `
    .tools-menu{position:relative;}
    /* the composer sits at the viewport bottom — open upward */
    .tools-menu .menu{top:auto; bottom:calc(100% + 8px); left:0; right:auto; width:224px; transform-origin:bottom left; transform:translateY(6px) scale(.98);}
    .tools-menu.open .menu{opacity:1; transform:none; pointer-events:auto;}
    .tools-menu .chev{transition:.2s;}
    .tools-menu.open .chev{transform:rotate(180deg);}
    .tools-menu .tcount{min-width:16px; height:16px; padding:0 4px; border-radius:9px; background:var(--green-soft); color:var(--green); font-family:var(--mono); font-size:10px; font-weight:600; display:grid; place-items:center;}
    .t-row{display:flex; align-items:center; gap:9px; padding:7px 10px; border-radius:var(--r-sm); cursor:pointer; transition:.12s; font-size:12.5px; font-weight:500;}
    .t-row:hover{background:var(--surface-2);}
    .t-row .t-label{flex:1;}
    .t-row.disabled{opacity:.4; cursor:not-allowed;}
    .t-docs-wrap{border-top:1px solid var(--line); margin-top:5px; padding-top:5px;}
  `,
  view: () => {
    // ── logic ───────────────────────────────────
    const open = van.state(false);
    const active = () =>
      TOOLS.filter((t) => chat.tools[t.id]).length +
      (knowledge.useInChat.val ? 1 : 0);

    const ToolRow = ({ id, label }) =>
      div(
        {
          class: () =>
            "t-row" + (models.selectedSupportsTools.val ? "" : " disabled"),
          onclick: () => models.selectedSupportsTools.val && chat.toggleTool(id),
          title: () =>
            models.selectedSupportsTools.val
              ? label + " tool"
              : "This model doesn't support tool calling",
        },
        span({ class: "t-label" }, label),
        Switch({ on: () => !!chat.tools[id], onToggle: () => {} }),
      );

    // ── content ─────────────────────────────────
    const trigger = button(
      {
        class: () => "cbtn toggle" + (active() ? " on" : ""),
        type: "button",
        onclick: (e) => {
          e.stopPropagation();
          open.val = !open.val;
        },
      },
      Icon("gear", { size: 14, strokeWidth: 2 }),
      "Tools",
      () => (active() ? span({ class: "tcount" }, String(active())) : span()),
      Icon("chevron", { size: 13, class: "chev", strokeWidth: 2.4 }),
    );

    return Menu({
      wrapClass: "tools-menu",
      open,
      trigger,
      children: [
        div({ class: "menu-h" }, "Tools"),
        ...TOOLS.map(ToolRow),
        div(
          { class: "t-docs-wrap" },
          div(
            {
              class: () => "t-row t-docs" + (knowledge.useInChat.val ? " on" : ""),
              onclick: knowledge.toggleUseInChat,
              title: "Ground replies in your uploaded documents",
            },
            Icon("file", { size: 14, strokeWidth: 2 }),
            span({ class: "t-label" }, "Use my docs"),
            Switch({ on: () => knowledge.useInChat.val, onToggle: () => {} }),
          ),
          // Interleaved thinking (Qwen3.6 preserve_thinking): prior turns'
          // reasoning rides back upstream so the model builds on it instead of
          // re-deriving — recommended for agentic/tool-heavy chats.
          div(
            {
              class: () => "t-row" + (chat.preserveThinking.val ? " on" : ""),
              onclick: () => (chat.preserveThinking.val = !chat.preserveThinking.val),
              title: "Carry earlier reasoning into later turns (interleaved thinking)",
            },
            Icon("brain", { size: 14, strokeWidth: 2 }),
            span({ class: "t-label" }, "Preserve thinking"),
            Switch({ on: () => chat.preserveThinking.val, onToggle: () => {} }),
          ),
        ),
      ],
    });
  },
});
