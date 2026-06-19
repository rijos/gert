// components/main/tools-menu.js - composer dropdown of tool toggles (Search /
// Sandbox / Todos / Clock) plus "Use my docs" (the rag tool -
// chat-and-tools.md: off removes search_documents for that turn). Replaces
// the top-bar chips to keep the top bar quiet. Reflects state/chat.tools;
// rows toggle in place (the menu stays open).
// Tool rows go inert when the selected model can't call tools (the server
// drops them anyway - IModelCatalog gates the turn planner; this mirrors it).
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { Menu } from "../ui/menu.js";
import { Switch } from "../ui/switch.js";
import * as chat from "../../state/chat.js";
import type { ToolId } from "../../state/chat.js";
import * as models from "../../state/models.js";
import { t } from "../../lib/i18n.js";

const { div, button, span } = van.tags;

// One toggle row: a tool id (a key of chat.tools) + its translated label.
interface ToolDef {
  id: ToolId;
  label: string;
}
const TOOLS: ToolDef[] = [
  { id: "search", label: t("Search") },
  { id: "fetch", label: t("Fetch pages") },
  { id: "sandbox", label: t("Run Python") },
  { id: "todo", label: t("Todos") },
  { id: "clock", label: t("Clock") },
  { id: "ask_user", label: t("Ask me") },
  { id: "memory", label: t("Save memories") },
  { id: "sub_agent", label: t("Sub-agents") },
];

// One toggle row in the menu. Inert (greyed, no toggle) when the selected model
// can't call tools. Closes over the shared chat/models state only - no instance
// state - so it lives at module scope rather than per-render.
const ToolRow = ({ id, label }: ToolDef) =>
  div(
    {
      class: () => "t-row" + (models.selectedSupportsTools.val ? "" : " disabled"),
      onclick: () => models.selectedSupportsTools.val && chat.toggleTool(id),
      title: () =>
        models.selectedSupportsTools.val
          ? label + " tool"
          : "This model doesn't support tool calling",
    },
    span({ class: "t-label" }, label),
    Switch({ on: () => !!chat.tools[id], onToggle: () => {} }),
  );

export const ToolsMenu = component({
  name: "tools-menu",
  css: `
    .tools-menu {
      position: relative;
    }
    /* the composer sits at the viewport bottom - open upward */
    .tools-menu .menu {
      top: auto;
      bottom: calc(100% + 8px);
      left: 0;
      right: auto;
      width: 224px;
      transform-origin: bottom left;
      transform: translateY(6px) scale(.98);
    }
    .tools-menu.open .menu {
      opacity: 1;
      transform: none;
      pointer-events: auto;
    }
    .tools-menu .chev {
      transition: var(--t-slow) var(--ease);
    }
    .tools-menu.open .chev {
      transform: rotate(180deg);
    }
    .tools-menu .tcount {
      min-width: 16px;
      height: 16px;
      padding: 0 4px;
      border-radius: 9px;
      background: var(--coral-soft);
      color: var(--coral-deep);
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      font-weight: 600;
      display: grid;
      place-items: center;
    }
    .t-row {
      display: flex;
      align-items: center;
      gap: 9px;
      padding: var(--sp-2) var(--sp-3);
      border-radius: var(--r-sm);
      cursor: pointer;
      transition: var(--t-fast);
      font-size: var(--fs-sm);
      font-weight: 500;
    }
    .t-row:hover {
      background: var(--surface-2);
    }
    .t-row .t-label {
      flex: 1;
    }
    /* inert != invisible: the label keeps AA contrast (--ink-3), the switch
       greys out - replaces the old opacity:.4 (2.3:1) treatment */
    .t-row.disabled {
      color: var(--ink-3);
      cursor: not-allowed;
    }
    .t-row.disabled:hover {
      background: none;
    }
    .t-row.disabled .switch {
      filter: grayscale(1);
      opacity: .6;
    }
    .t-docs-wrap {
      border-top: 1px solid var(--line);
      margin-top: 5px;
      padding-top: 5px;
    }
  `,
  // logic: the open/close state, its toggle handler, and the pure `active()`
  // count of enabled tools (a plain function - the bindings that call it stay
  // in view).
  setup: () => {
    const open = van.state(false);
    const toggle = (e: Event) => {
      e.stopPropagation();
      open.val = !open.val;
    };
    const active = () =>
      TOOLS.filter((t) => chat.tools[t.id]).length +
      (chat.canvasOn() ? 1 : 0) +
      (chat.tools.rag ? 1 : 0); // "Use my docs" - the rag row below
    return { open, toggle, active };
  },
  // content: the count-badged trigger button + the upward Menu of tool rows.
  view: ({ open, toggle, active }) => {
    const trigger = button({ class: () => "cbtn toggle" + (active() ? " on" : ""), type: "button", onclick: toggle },
      Icon("gear", { size: 14, strokeWidth: 2 }),
      t("Tools"),
      () => (active() ? span({ class: "tcount" }, String(active())) : span()),
      Icon("chevron", { size: 13, class: "chev", strokeWidth: 2.4 }),
    );

    return Menu({
      wrapClass: "tools-menu",
      open,
      trigger,
      children: [
        div({ class: "menu-h" }, t("Tools")),
        ...TOOLS.map(ToolRow),
        // Canvas suite (make/edit/read artifact) - one switch for the trio.
        div(
          {
            class: () => "t-row" + (models.selectedSupportsTools.val ? "" : " disabled"),
            onclick: () => models.selectedSupportsTools.val && chat.toggleCanvas(),
            title: () =>
              models.selectedSupportsTools.val
                ? "Let the model create and edit files in the canvas"
                : "This model doesn't support tool calling",
          },
          span({ class: "t-label" }, t("Canvas")),
          Switch({ on: () => chat.canvasOn(), onToggle: () => {} }),
        ),
        div({ class: "t-docs-wrap" },
          // "Use my docs" IS the rag tool - off removes search_documents for
          // the turn (chat-and-tools.md).
          div({ class: () => "t-row t-docs" + (chat.tools.rag ? " on" : ""), onclick: () => chat.toggleTool("rag"), title: "Ground replies in your uploaded documents" },
            Icon("file", { size: 14, strokeWidth: 2 }),
            span({ class: "t-label" }, t("Use my docs")),
            Switch({ on: () => !!chat.tools.rag, onToggle: () => {} }),
          ),
        ),
      ],
    });
  },
});
