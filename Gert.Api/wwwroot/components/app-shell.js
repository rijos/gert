// components/app-shell.js — the 3-column .app grid + layout state classes +
// scrim. The middle region is a host the router swaps pages into.
import van from "van";
import { component } from "../lib/component.js";
import { Sidebar } from "./sidebar/sidebar.js";
import { CanvasPanel } from "./canvas/canvas-panel.js";
import * as ui from "../state/ui.js";

const { div } = van.tags;

// appClass — derives the mockup's .app state classes from ui state.
const appClass = () => {
  const c = ["app"];
  if (ui.navCollapsed.val) c.push("nav-collapsed");
  if (ui.panelCollapsed.val) c.push("panel-collapsed");
  if (ui.panelWide.val) c.push("panel-wide");
  if (ui.navOpen.val) c.push("nav-open");
  if (ui.panelOpen.val) c.push("panel-open");
  return c.join(" ");
};

// The router renders into `mainHost`; AppShell exposes it for app.js.
export const mainHost = div({ class: "main" });

// .app grid + .scrim live in layout.css (responsive layout); AppShell owns the
// .main column it hosts pages in.
export const AppShell = component({
  name: "app-shell",
  css: `
    .main{display:flex; flex-direction:column; min-width:0; background:var(--paper); position:relative;}
  `,
  view: () =>
    div(
      { class: appClass, style: () => `--panel-w:${ui.panelWidth.val}px` },
      div({ class: "scrim", onclick: ui.closeDrawers }),
      Sidebar(),
      mainHost,
      CanvasPanel(),
    ),
});
