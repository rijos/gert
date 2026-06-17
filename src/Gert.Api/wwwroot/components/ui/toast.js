// components/ui/toast.js - transient toast notifications.
// toast(message, kind) mounts into a fixed host and auto-dismisses. Imperative
// (not a view-returning component), so it co-locates its CSS via a one-time
// <style> injection when the host is first created - mirroring the component()
// factory's inject-once behaviour.
import van from "/lib/van.js";
import { adoptStyles } from "../../lib/component.js";

const { div } = van.tags;

const CSS = `
  .toast-host{position:fixed; bottom:18px; right:18px; z-index:80; display:flex; flex-direction:column; gap:8px;}
  .toast{font-size:var(--fs-md); font-weight:500; color:var(--ink); background:var(--surface); border:1px solid var(--line); border-left:3px solid var(--coral); border-radius:var(--r-sm); padding:10px 14px; box-shadow:var(--shadow-toast); animation:rise var(--t-slow) var(--ease) backwards; max-width:320px;}
  .toast.err{border-left-color:var(--brick);}
  /* .ok rides the default coral edge - success is the accent, not green */
`;

let host = null;
const ensureHost = () => {
  if (!host) {
    adoptStyles(CSS); // constructable stylesheet - CSP-safe under style-src 'self'
    host = div({ class: "toast-host" });
    van.add(document.body, host);
  }
  return host;
};

// kind: "info" | "ok" | "err"
export const toast = (message, kind = "info", ms = 3200) => {
  const el = div({ class: "toast" + (kind === "info" ? "" : " " + kind) }, message);
  van.add(ensureHost(), el);
  setTimeout(() => el.remove(), ms);
};
