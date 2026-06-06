// state/ui.js — scalar UI state: theme, nav/panel collapse, panel-wide,
// mobile drawers, active artifact tab / KB view. van.state only. No DOM, no I/O.
// (Theme persists to localStorage — the token never does; see services/auth.js.)
import van from "van";

const THEME_KEY = "gert.theme";
const PANEL_W_KEY = "gert.panelw";

// canvas panel width (px) — drag-resizable; drives --panel-w on .app.
export const PANEL_MIN = 300;
export const DEFAULT_PANEL_W = 372;
export const panelWidth = van.state(DEFAULT_PANEL_W);

// The chat column's floor — mirrors the grid's minmax(500px,1fr) (layout.css):
// below this the composer row (Attach/Tools/Thinking/ring/send) gets cramped.
export const MAIN_MIN = 500;
const NAV_W = 264; // --nav-col (layout.css)

// Dragging may never squeeze the chat below MAIN_MIN; the nav only counts
// while expanded. PANEL_MIN wins the tug-of-war on tiny windows — the grid's
// minmax(0,…) panel column absorbs the difference visually.
const clampPanelWidth = (px) => {
  const nav = navCollapsed.val ? 0 : NAV_W;
  const max = window.innerWidth - nav - MAIN_MIN;
  return Math.max(PANEL_MIN, Math.min(px, max));
};

export const restorePanelWidth = () => {
  const v = parseInt(localStorage.getItem(PANEL_W_KEY), 10);
  if (Number.isFinite(v)) panelWidth.val = clampPanelWidth(v);
};

export const setPanelWidth = (px) => {
  const w = clampPanelWidth(px);
  panelWidth.val = w;
  localStorage.setItem(PANEL_W_KEY, String(w));
};

// layout flags (mirror the mockup's app classes)
export const navCollapsed = van.state(false);
export const panelCollapsed = van.state(false);
export const panelWide = van.state(false);
export const navOpen = van.state(false); // mobile drawer
export const panelOpen = van.state(false); // mobile drawer

// canvas: which artifact id is active, or "kb" for the knowledge view
export const activeArtifact = van.state(null);
export const showKnowledge = van.state(false);

// theme: null = follow OS, "manila" (paper) | "ember" (dark) = explicit.
export const theme = van.state(null);

export const isMobile = () =>
  window.matchMedia("(max-width:1079px)").matches;

// Pre-rename saves ("light"/"dark") migrate to the theme names they meant.
const MIGRATE = { light: "manila", dark: "ember" };

export const restoreTheme = () => {
  const saved = localStorage.getItem(THEME_KEY);
  const name = MIGRATE[saved] || saved;
  if (name === "manila" || name === "ember") {
    theme.val = name;
    document.documentElement.setAttribute("data-theme", name);
    if (name !== saved) localStorage.setItem(THEME_KEY, name);
  }
};

export const toggleTheme = () => {
  const root = document.documentElement;
  const current = root.getAttribute("data-theme");
  const dark =
    current === "ember" ||
    (!current && window.matchMedia("(prefers-color-scheme: dark)").matches);
  const next = dark ? "manila" : "ember";
  root.setAttribute("data-theme", next);
  theme.val = next;
  localStorage.setItem(THEME_KEY, next);
};

export const toggleNav = () => {
  if (isMobile()) {
    panelOpen.val = false;
    navOpen.val = !navOpen.val;
  } else navCollapsed.val = !navCollapsed.val;
};

export const togglePanel = () => {
  if (isMobile()) {
    navOpen.val = false;
    panelOpen.val = !panelOpen.val;
  } else panelCollapsed.val = !panelCollapsed.val;
};

export const toggleWide = () => (panelWide.val = !panelWide.val);

export const closeDrawers = () => {
  navOpen.val = false;
  panelOpen.val = false;
};

// activeArtifact stays untouched: the canvas dispatch keys on
// (activeArtifact && !showKnowledge), so untoggling restores the tab that was
// open before the knowledge view.
export const toggleKnowledge = () => {
  showKnowledge.val = !showKnowledge.val;
};

export const openArtifact = (id) => {
  activeArtifact.val = id;
  showKnowledge.val = false;
  // A live artifact event should always surface the canvas.
  panelCollapsed.val = false;
};
