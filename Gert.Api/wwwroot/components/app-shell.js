// components/app-shell.js — the 3-column .app grid + layout state classes +
// scrim. The middle region is a host the router swaps pages into.
import van from "van";
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

export const AppShell = () =>
  div(
    { class: appClass, style: () => `--panel-w:${ui.panelWidth.val}px` },
    div({ class: "scrim", onclick: ui.closeDrawers }),
    Sidebar(),
    mainHost,
    CanvasPanel(),
  );
