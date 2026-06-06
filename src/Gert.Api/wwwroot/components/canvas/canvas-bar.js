// components/canvas/canvas-bar.js — artifact tab strip + bar tools
// (KB toggle, expand, close drawer).
import van from "van";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { ArtifactTabs } from "./artifact-tabs.js";
import * as ui from "../../state/ui.js";

const { div, button } = van.tags;

export const CanvasBar = component({
  name: "canvas-bar",
  css: `
    .canvas-bar{height:var(--head-h); flex:none; display:flex; align-items:center; gap:6px; padding:0 10px 0 12px; border-bottom:1px solid var(--line); background:var(--surface-2);}
    .canvas-bar .bar-tools{display:flex; gap:2px; flex:none;}
    .kbtn{background:none; border:1px solid transparent; color:var(--ink-3); cursor:pointer; padding:5px; border-radius:6px; display:grid; place-items:center; transition:.13s;}
    .kbtn:hover{background:var(--surface-2); color:var(--ink);}
    .kbtn.active{color:var(--coral-deep); background:var(--coral-soft);}
    .kbtn svg{width:15px; height:15px;}
  `,
  view: () =>
  div(
    { class: "canvas-bar" },
    ArtifactTabs(),
    div(
      { class: "bar-tools" },
      button(
        {
          class: () => "kbtn" + (ui.showKnowledge.val ? " active" : ""),
          title: "Knowledge base",
          onclick: ui.toggleKnowledge,
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
  ),
});
