// components/main/tools-menu.js - composer popup of tool toggles, rendered PURELY from the
// server's entitlement catalog (GET /api/tools -> state/tools.availableTools). No per-tool-id
// knowledge lives here: every row's label (descriptor.title), grouping (descriptor.group), and
// section (descriptor.source) ride the catalog, so a future MCP source becomes its own section
// for free. The per-conversation on/off toggles stay in state/chat.tools (persisted via
// ToolToggles). Three groupings, all derived from the descriptor's `group`:
//   - "canvas": collapses to ONE "Canvas" switch toggling the whole group together.
//   - "docs": pinned to its own bordered section ("Use my docs").
//   - everything else: plain switch rows labelled by descriptor.title.
// Rows go inert when the selected model can't call tools (the server drops them anyway - this
// mirrors the turn-planner gate).
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { Menu } from "../ui/menu.js";
import * as chat from "../../state/chat.js";
import * as toolsState from "../../state/tools.js";
import type { ToolInfo } from "../../state/tools.js";
import * as models from "../../state/models.js";
import { t } from "../../lib/i18n.js";

const { div, button, span } = van.tags;

// Catalog slices, all derived from the descriptor's `group` (never a hardcoded id list).
const inGroup = (group: string) => toolsState.availableTools.filter((tool) => tool.group === group);
const canvasTools = () => inGroup("canvas");
const docsTools = () => inGroup("docs");
// Plain switch rows = everything that isn't a collapsed/pinned group.
const standardTools = () =>
  toolsState.availableTools.filter((tool) => tool.group !== "canvas" && tool.group !== "docs");

// The catalog-derived canvas group toggles as one unit: on iff every member is enabled.
const canvasOn = () => canvasTools().every((tool) => !!chat.tools[tool.id]);
const toggleCanvas = () => {
  const on = !canvasOn();
  canvasTools().forEach((tool) => chat.setTool(tool.id, on));
};

// One toggle row as a single <button role="switch"> so it is keyboard-operable (WCAG 2.1.1) and
// exposes its state via aria-checked (4.1.2) - the knob is presentational. Inert (greyed, no
// toggle) when the selected model can't call tools. `id` is the catalog id; `label`/`title` are
// the descriptor's text. Closes over shared state only, so it lives at module scope.
const ToolRow = (id: string, label: string, title: string) =>
  button(
    {
      class: () => "t-row" + (models.selectedSupportsTools.val ? "" : " disabled"),
      type: "button",
      role: "switch",
      "aria-checked": () => String(!!chat.tools[id]),
      "aria-disabled": () => String(!models.selectedSupportsTools.val),
      onclick: () => models.selectedSupportsTools.val && chat.toggleTool(id),
      title: () =>
        models.selectedSupportsTools.val ? title : "This model doesn't support tool calling",
    },
    span({ class: "t-label" }, label),
    span({ class: "t-knob", "aria-hidden": "true" }),
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
      visibility: visible;
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
    /* the row IS the switch button: reset the native chrome so it reads as a row */
    .t-row {
      display: flex;
      align-items: center;
      gap: 9px;
      width: 100%;
      padding: var(--sp-2) var(--sp-3);
      border: none;
      border-radius: var(--r-sm);
      background: none;
      color: inherit;
      font-family: inherit;
      text-align: left;
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
    /* presentational toggle knob (the row's aria-checked is the real state) */
    .t-knob {
      width: 34px;
      height: 19px;
      border-radius: 20px;
      background: var(--coral-deep);
      position: relative;
      flex: none;
    }
    .t-knob::after {
      content: "";
      position: absolute;
      width: 15px;
      height: 15px;
      border-radius: 50%;
      background: var(--on-chip);
      top: 2px;
      left: 17px;
      transition: var(--t-fast);
      box-shadow: var(--shadow-thumb);
    }
    .t-row[aria-checked="false"] .t-knob {
      background: var(--line);
    }
    .t-row[aria-checked="false"] .t-knob::after {
      left: 2px;
    }
    /* inert != invisible: the label keeps AA contrast (--ink-3), the knob
       greys out - replaces the old opacity:.4 (2.3:1) treatment */
    .t-row.disabled {
      color: var(--ink-3);
      cursor: not-allowed;
    }
    .t-row.disabled:hover {
      background: none;
    }
    .t-row.disabled .t-knob {
      filter: grayscale(1);
      opacity: .6;
    }
    .t-docs-wrap {
      border-top: 1px solid var(--line);
      margin-top: 5px;
      padding-top: 5px;
    }
    /* an empty catalog (no entitled tools) still owns a quiet line, never a blank menu */
    .t-empty {
      padding: var(--sp-2) var(--sp-3);
      color: var(--ink-3);
      font-size: var(--fs-sm);
    }
  `,
  setup: () => {
    const open = van.state(false);
    const toggle = (e: Event) => {
      e.stopPropagation();
      open.val = !open.val;
    };
    // Active count: each entitled standard row that's on, +1 for the canvas group (if any member
    // entitled) when on, +1 for the docs group when on. Counts groups, not their member tools.
    const active = () =>
      standardTools().filter((tool) => !!chat.tools[tool.id]).length +
      (canvasTools().length > 0 && canvasOn() ? 1 : 0) +
      (docsTools().some((tool) => !!chat.tools[tool.id]) ? 1 : 0);
    return { open, toggle, active };
  },
  view: ({ open, toggle, active }) => {
    const trigger = button({ class: () => "cbtn toggle" + (active() ? " on" : ""), type: "button", "aria-haspopup": "true", "aria-expanded": () => String(open.val), "aria-label": t("Tools"), onclick: toggle },
      Icon("gear", { size: 14, strokeWidth: 2 }),
      t("Tools"),
      () => (active() ? span({ class: "tcount" }, String(active())) : span()),
      Icon("chevron", { size: 13, class: "chev", strokeWidth: 2.4 }),
    );

    // One source's body: its standard rows + a single Canvas switch for its canvas group.
    // (Docs renders in its own pinned section below.) Today only "builtin" exists; sectioning
    // routes through `source` so adding an MCP source later is data-only.
    const sourceBody = (rows: ToolInfo[], canvas: ToolInfo[]) =>
      div(
        ...rows.map((tool) => ToolRow(tool.id, tool.title, tool.description || tool.title)),
        // Canvas group (e.g. make/edit/read artifact) - one switch for the whole group.
        canvas.length > 0
          ? button(
              {
                class: () => "t-row" + (models.selectedSupportsTools.val ? "" : " disabled"),
                type: "button",
                role: "switch",
                "aria-checked": () => String(canvasOn()),
                "aria-disabled": () => String(!models.selectedSupportsTools.val),
                onclick: () => models.selectedSupportsTools.val && toggleCanvas(),
                title: () =>
                  models.selectedSupportsTools.val
                    ? "Let the model create and edit files in the canvas"
                    : "This model doesn't support tool calling",
              },
              span({ class: "t-label" }, t("Canvas")),
              span({ class: "t-knob", "aria-hidden": "true" }),
            )
          : span(),
      );

    return Menu({
      wrapClass: "tools-menu",
      open,
      trigger,
      children: [
        div({ class: "menu-h" }, t("Tools")),
        // The catalog drives every row; () => reactively rebuilds when it (or the entitlement)
        // loads. Sectioned by `source` (only "builtin" today): each source contributes its
        // standard rows + Canvas group; the docs group is pinned to its own section below.
        () => {
          const sources = [...new Set(toolsState.availableTools.map((tool) => tool.source))];
          const empty = toolsState.availableTools.length === 0;
          return div(
            ...sources.map((source) =>
              sourceBody(
                standardTools().filter((tool) => tool.source === source),
                canvasTools().filter((tool) => tool.source === source),
              ),
            ),
            // No entitled tools at all -> a quiet line, never a blank menu.
            empty ? div({ class: "t-empty" }, t("No tools available")) : span(),
          );
        },
        // The "docs" group ("Use my docs") - off removes its tools for the turn
        // (chat-and-tools.md). Pinned to its own bordered section, shown only when entitled.
        () =>
          docsTools().length > 0
            ? div({ class: "t-docs-wrap" },
                ...docsTools().map((tool) =>
                  button({ class: () => "t-row t-docs" + (chat.tools[tool.id] ? " on" : ""), type: "button", role: "switch", "aria-checked": () => String(!!chat.tools[tool.id]), onclick: () => chat.toggleTool(tool.id), title: "Ground replies in your uploaded documents" },
                    Icon(tool.icon, { size: 14, strokeWidth: 2 }),
                    span({ class: "t-label" }, tool.title),
                    span({ class: "t-knob", "aria-hidden": "true" }),
                  ),
                ),
              )
            : span(),
      ],
    });
  },
});
