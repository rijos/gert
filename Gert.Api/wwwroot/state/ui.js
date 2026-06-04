// state/ui.js — scalar UI state: theme, nav/panel collapse, panel-wide,
// mobile drawers, active artifact tab / KB view. van.state only. No DOM, no I/O.
// (Theme persists to localStorage — the token never does; see services/auth.js.)
import van from "van";

const THEME_KEY = "gert.theme";

// layout flags (mirror the mockup's app classes)
export const navCollapsed = van.state(false);
export const panelCollapsed = van.state(false);
export const panelWide = van.state(false);
export const navOpen = van.state(false); // mobile drawer
export const panelOpen = van.state(false); // mobile drawer

// canvas: which artifact id is active, or "kb" for the knowledge view
export const activeArtifact = van.state(null);
export const showKnowledge = van.state(false);

// theme: null = follow OS, "light" | "dark" = explicit
export const theme = van.state(null);

export const isMobile = () =>
  window.matchMedia("(max-width:1079px)").matches;

export const restoreTheme = () => {
  const saved = localStorage.getItem(THEME_KEY);
  if (saved === "light" || saved === "dark") {
    theme.val = saved;
    document.documentElement.setAttribute("data-theme", saved);
  }
};

export const toggleTheme = () => {
  const root = document.documentElement;
  const current = root.getAttribute("data-theme");
  const dark =
    current === "dark" ||
    (!current && window.matchMedia("(prefers-color-scheme: dark)").matches);
  const next = dark ? "light" : "dark";
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

export const openKnowledge = () => {
  showKnowledge.val = true;
  activeArtifact.val = null;
};

export const openArtifact = (id) => {
  activeArtifact.val = id;
  showKnowledge.val = false;
};
