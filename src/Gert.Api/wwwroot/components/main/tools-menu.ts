// components/main/tools-menu.js - composer "Tools" button that opens a CENTERED MODAL of tool
// toggles, rendered PURELY from the server's entitlement catalog (GET /api/tools ->
// state/tools.availableTools). No per-tool-id knowledge lives here: every row's label
// (descriptor.title), grouping (descriptor.group), and section (descriptor.source) ride the
// catalog, so a future MCP source becomes its own section for free. The per-conversation on/off
// toggles stay in state/chat.tools (persisted via ToolToggles). Three groupings, all derived from
// the descriptor's `group`:
//   - "canvas": collapses to ONE "Canvas" switch toggling the whole group together.
//   - "docs": pinned to its own bordered section ("Use my docs").
//   - everything else: plain switch rows labelled by descriptor.title.
// The body lives in the shared Modal scaffold (centered, scrim, focus-trap, Esc/backdrop close)
// rather than an anchored dropdown, so a long tool list scrolls instead of overflowing the
// composer. The toggles apply live, so the modal has only a "Done" action - no save/confirm.
// Rows go inert when the selected model can't call tools (the server drops them anyway - this
// mirrors the turn-planner gate).
import van from "/lib/van.js";
import type { ChildDom } from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { Modal } from "../ui/modal.js";
import { Button } from "../ui/button.js";
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

// One source's body: its standard rows + a single Canvas switch for its canvas group.
// (Docs renders in its own pinned section below.) Today only "builtin" exists; sectioning
// routes through `source` so adding an MCP source later is data-only.
const sourceBody = (rows: ToolInfo[], canvas: ToolInfo[]) =>
  div(
    ...rows.map((tool) => ToolRow(tool.id, tool.title, tool.description || tool.title)),
    // Canvas group (e.g. make/edit/read/list artifact) - one switch for the whole group.
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

// The scrollable rows shown inside the modal body. The catalog drives every row; () =>
// reactively rebuilds when it (or the entitlement) loads. Sectioned by `source` (only
// "builtin" today): each source contributes its standard rows + Canvas group; the docs
// group is pinned to its own section below. Wrapped in .t-scroll so a long list scrolls.
const toolRows = (): ChildDom =>
  div(
    { class: "t-scroll" },
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
        // No entitled tools at all -> a quiet line, never a blank modal.
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
  );

// Open the tools modal. Toggles apply live, so the only action is "Done" (close); the
// Modal scaffold supplies the focus-trap, Esc, and backdrop close.
const openToolsModal = () =>
  Modal({
    title: t("Tools"),
    closable: true,
    dismissable: true,
    body: toolRows(),
    actions: (close) => Button({ label: t("Done"), onclick: close }),
  });

export const ToolsMenu = component({
  name: "tools-menu",
  css: `
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
    /* the rows scroll inside the modal so a long tool list never overflows the dialog */
    .t-scroll {
      max-height: 60vh;
      overflow-y: auto;
      margin: 0 -4px;
      padding: 0 4px;
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
    /* an empty catalog (no entitled tools) still owns a quiet line, never a blank modal */
    .t-empty {
      padding: var(--sp-2) var(--sp-3);
      color: var(--ink-3);
      font-size: var(--fs-sm);
    }
  `,
  setup: () => {
    // Active count: each entitled standard row that's on, +1 for the canvas group (if any member
    // entitled) when on, +1 for the docs group when on. Counts groups, not their member tools.
    const active = () =>
      standardTools().filter((tool) => !!chat.tools[tool.id]).length +
      (canvasTools().length > 0 && canvasOn() ? 1 : 0) +
      (docsTools().some((tool) => !!chat.tools[tool.id]) ? 1 : 0);
    return { active };
  },
  // The composer "Tools" button (gear icon + live count badge). Clicking opens the modal;
  // the rows render purely from the catalog inside it.
  view: ({ active }) =>
    div({ class: "tools-menu" },
      button({ class: () => "cbtn toggle" + (active() ? " on" : ""), type: "button", "aria-haspopup": "dialog", "aria-label": t("Tools"), onclick: openToolsModal },
        Icon("gear", { size: 14, strokeWidth: 2 }),
        t("Tools"),
        () => (active() ? span({ class: "tcount" }, String(active())) : span()),
      ),
    ),
});
