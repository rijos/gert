// components/main/tools-menu.js - composer popup of tool toggles, driven by the
// server's entitlement catalog (GET /api/tools -> state/tools.availableTools).
// The rows ARE the tools this user may use; the per-conversation on/off toggles
// stay in state/chat.tools (persisted via ToolToggles). Two groupings are derived
// client-side, NOT from a hardcoded list:
//   - Canvas: the make/edit/read_artifact trio collapses to ONE switch
//     (chat.canvasOn/toggleCanvas) - shown when any of the three is entitled.
//   - "Use my docs": the rag tool, pinned to its own bordered section.
// Other rows get a friendly client-side label keyed by id (the labels the old
// hardcoded menu carried); a tool with no mapped label falls back to its
// model-facing name. The endpoint's `name` is model-facing, so labels live here.
// Rows go inert when the selected model can't call tools (the server drops them
// anyway - this mirrors the turn-planner gate).
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { Menu } from "../ui/menu.js";
import * as chat from "../../state/chat.js";
import type { ToolId } from "../../state/chat.js";
import * as toolsState from "../../state/tools.js";
import * as models from "../../state/models.js";
import { t } from "../../lib/i18n.js";

const { div, button, span } = van.tags;

// The artifact trio shown/toggled as one "Canvas" switch (state/chat.CANVAS_TOOL_IDS).
const CANVAS_IDS = new Set<string>(chat.CANVAS_TOOL_IDS);

// Friendly labels keyed by tool id - the names the old hardcoded menu used. A tool
// with no entry here falls back to its model-facing `name` from the catalog.
const LABELS: Record<string, string> = {
  search: t("Search"),
  fetch: t("Fetch pages"),
  sandbox: t("Run Python"),
  todo: t("Todos"),
  clock: t("Clock"),
  ask_user: t("Ask me"),
  memory: t("Save memories"),
  sub_agent: t("Sub-agents"),
};

// The entitled NON-special tools (everything except rag + the canvas trio): the
// plain switch rows. Stable order = the catalog's (the endpoint sorts by id).
const standardRows = () =>
  toolsState.availableTools.filter((tool) => tool.id !== "rag" && !CANVAS_IDS.has(tool.id));

// One toggle row as a single <button role="switch"> so it is keyboard-operable (WCAG 2.1.1) and
// exposes its state via aria-checked (4.1.2) - the knob is presentational. Inert (greyed, no
// toggle) when the selected model can't call tools. `id` is the catalog id; `label`/`title` are
// the client-side friendly text. Closes over shared state only, so it lives at module scope.
const ToolRow = (id: string, label: string, title: string) =>
  button(
    {
      class: () => "t-row" + (models.selectedSupportsTools.val ? "" : " disabled"),
      type: "button",
      role: "switch",
      "aria-checked": () => String(!!chat.tools[id as ToolId]),
      "aria-disabled": () => String(!models.selectedSupportsTools.val),
      onclick: () => models.selectedSupportsTools.val && chat.toggleTool(id as ToolId),
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
    // Has the catalog granted any of the artifact trio? -> the Canvas row is offered.
    const canvasEntitled = () => toolsState.availableTools.some((tool) => CANVAS_IDS.has(tool.id));
    const ragEntitled = () => toolsState.availableTools.some((tool) => tool.id === "rag");
    // Active = on AND entitled (a stale toggle for a now-ungranted tool doesn't count).
    const active = () =>
      standardRows().filter((tool) => chat.tools[tool.id as ToolId]).length +
      (canvasEntitled() && chat.canvasOn() ? 1 : 0) +
      (ragEntitled() && chat.tools.rag ? 1 : 0);
    return { open, toggle, active, canvasEntitled, ragEntitled };
  },
  view: ({ open, toggle, active, canvasEntitled, ragEntitled }) => {
    const trigger = button({ class: () => "cbtn toggle" + (active() ? " on" : ""), type: "button", "aria-haspopup": "true", "aria-expanded": () => String(open.val), "aria-label": t("Tools"), onclick: toggle },
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
        // The catalog drives the rows; () => reactively rebuilds when it (or the
        // entitlement) loads. A standard row's label is the friendly id mapping,
        // else the tool's model-facing name; the title is the description.
        () =>
          div(
            ...standardRows().map((tool) =>
              ToolRow(tool.id, LABELS[tool.id] ?? tool.name, tool.description || (LABELS[tool.id] ?? tool.name)),
            ),
            // Canvas suite (make/edit/read artifact) - one switch for the trio,
            // shown only when at least one of the three is entitled.
            canvasEntitled()
              ? button(
                  {
                    class: () => "t-row" + (models.selectedSupportsTools.val ? "" : " disabled"),
                    type: "button",
                    role: "switch",
                    "aria-checked": () => String(chat.canvasOn()),
                    "aria-disabled": () => String(!models.selectedSupportsTools.val),
                    onclick: () => models.selectedSupportsTools.val && chat.toggleCanvas(),
                    title: () =>
                      models.selectedSupportsTools.val
                        ? "Let the model create and edit files in the canvas"
                        : "This model doesn't support tool calling",
                  },
                  span({ class: "t-label" }, t("Canvas")),
                  span({ class: "t-knob", "aria-hidden": "true" }),
                )
              : span(),
            // No standard rows, no canvas, no rag -> the user is entitled to nothing.
            standardRows().length === 0 && !canvasEntitled() && !ragEntitled()
              ? div({ class: "t-empty" }, t("No tools available"))
              : span(),
          ),
        // "Use my docs" IS the rag tool - off removes search_documents for the
        // turn (chat-and-tools.md). Pinned to its own bordered section, shown
        // only when rag is entitled.
        () =>
          ragEntitled()
            ? div({ class: "t-docs-wrap" },
                button({ class: () => "t-row t-docs" + (chat.tools.rag ? " on" : ""), type: "button", role: "switch", "aria-checked": () => String(!!chat.tools.rag), onclick: () => chat.toggleTool("rag"), title: "Ground replies in your uploaded documents" },
                  Icon("file", { size: 14, strokeWidth: 2 }),
                  span({ class: "t-label" }, t("Use my docs")),
                  span({ class: "t-knob", "aria-hidden": "true" }),
                ),
              )
            : span(),
      ],
    });
  },
});
