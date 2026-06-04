// components/canvas/canvas-bar.js — artifact tab strip + bar tools
// (KB toggle, expand, close drawer).
import van from "van";
import { Icon } from "../../icons/icons.js";
import { ArtifactTabs } from "./artifact-tabs.js";
import * as ui from "../../state/ui.js";

const { div, button } = van.tags;

export const CanvasBar = () =>
  div(
    { class: "canvas-bar" },
    ArtifactTabs(),
    div(
      { class: "bar-tools" },
      button(
        {
          class: () => "kbtn" + (ui.showKnowledge.val ? " active" : ""),
          title: "Knowledge base",
          onclick: ui.openKnowledge,
        },
        Icon("book", { size: 15, strokeWidth: 2 }),
      ),
      button(
        {
          class: () => "kbtn expand-btn" + (ui.panelWide.val ? " active" : ""),
          title: "Expand pane",
          onclick: ui.toggleWide,
        },
        Icon("expand", { size: 15, strokeWidth: 2 }),
      ),
      button(
        { class: "kbtn drawer-close", title: "Close", onclick: ui.togglePanel },
        Icon("close", { size: 15, strokeWidth: 2.2 }),
      ),
    ),
  );
