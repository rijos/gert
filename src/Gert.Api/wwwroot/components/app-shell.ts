// The 3-column .app grid + layout state classes + scrim. The middle region is a
// host the router swaps pages into.
import van from "/lib/van.js";
import { component } from "../lib/component.js";
import { Sidebar } from "./sidebar/sidebar.js";
import { CanvasPanel } from "./canvas/canvas-panel.js";
import { ConnectionBanner } from "./connection-banner.js";
import * as ui from "../state/ui.js";

const { div } = van.tags;

const appClass = () => {
  const c = ["app"];
  if (ui.navCollapsed.val) c.push("nav-collapsed");
  if (ui.panelCollapsed.val) c.push("panel-collapsed");
  if (ui.navOpen.val) c.push("nav-open");
  if (ui.panelOpen.val) c.push("panel-open");
  if (ui.adminRoute.val) c.push("route-admin"); // folds the canvas column away
  return c.join(" ");
};

// .app grid + .scrim live in layout.css (responsive layout); AppShell owns the
// .main column's look (the css below) but app.ts owns the host element it swaps
// pages into - it's passed in so the module that wires the router owns the host.
export const AppShell = component({
  name: "app-shell",
  css: `
    /* paper grain rides the pane background (tokens.css --grain-img) */
    .main {
      display: flex;
      flex-direction: column;
      min-width: 0;
      min-height: 0;
      background: var(--bg);
      background-image: var(--grain-img);
      background-size: 18px 18px;
      position: relative;
    }
  `,
  view: (mainHost: HTMLElement) =>
    div({ class: appClass, style: () => `--panel-w:${ui.panelWidth.val}px` },
      ConnectionBanner(),
      div({ class: "scrim", onclick: ui.closeDrawers }),
      Sidebar(),
      mainHost,
      CanvasPanel(),
    ),
});
