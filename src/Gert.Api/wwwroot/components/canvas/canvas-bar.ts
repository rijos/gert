import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { ArtifactTabs } from "./artifact-tabs.js";
import * as ui from "../../state/ui.js";

const { div, button } = van.tags;

export const CanvasBar = component({
  name: "canvas-bar",
  css: `
    .canvas-bar {
      height: var(--head-h);
      flex: none;
      display: flex;
      align-items: center;
      gap: 6px;
      padding: 0 10px 0 12px;
      border-bottom: 1px solid var(--line);
      background: var(--surface-2);
    }

    .canvas-bar .bar-tools {
      display: flex;
      gap: 2px;
      flex: none;
    }

    .kbtn {
      background: none;
      border: 1px solid transparent;
      color: var(--ink-3);
      cursor: pointer;
      padding: 5px;
      border-radius: var(--r-xs);
      display: grid;
      place-items: center;
      transition: var(--t-fast);
    }

    .kbtn:hover {
      background: var(--surface-2);
      color: var(--ink);
    }

    .kbtn.active {
      color: var(--coral-deep);
      background: var(--coral-soft);
    }

    .kbtn svg {
      width: 15px;
      height: 15px;
    }
  `,
  view: () =>
  div(
    { class: "canvas-bar" },
    ArtifactTabs(),
    div(
      { class: "bar-tools" },
      button({ class: () => "kbtn" + (ui.showKnowledge.val ? " active" : ""), title: "Knowledge base", "aria-label": "Knowledge base", "aria-pressed": () => String(ui.showKnowledge.val), onclick: ui.toggleKnowledge },
        Icon("book", { size: 15, strokeWidth: 2 }),
      ),
      button(
        { class: "kbtn drawer-close", title: "Close panel", "aria-label": "Close panel", onclick: ui.togglePanel },
        Icon("close", { size: 15, strokeWidth: 2.2 }),
      ),
    ),
  ),
});
