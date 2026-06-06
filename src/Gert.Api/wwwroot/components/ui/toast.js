// components/ui/toast.js — transient toast notifications.
// toast(message, kind) mounts into a fixed host and auto-dismisses. Imperative
// (not a view-returning component), so it co-locates its CSS via a one-time
// <style> injection when the host is first created — mirroring the component()
// factory's inject-once behaviour.
import van from "van";
import { adoptStyles } from "../../lib/component.js";

const { div } = van.tags;

const CSS = `
  .toast-host{position:fixed; bottom:18px; right:18px; z-index:80; display:flex; flex-direction:column; gap:8px;}
  .toast{font-size:12.5px; font-weight:500; color:var(--ink); background:var(--surface); border:1px solid var(--line); border-left:3px solid var(--coral); border-radius:var(--r-sm); padding:10px 14px; box-shadow:0 8px 24px -12px rgba(60,46,28,.4); animation:rise .3s cubic-bezier(.2,.8,.2,1) backwards; max-width:320px;}
  .toast.err{border-left-color:var(--brick);}
  .toast.ok{border-left-color:var(--green);}
`;

let host = null;
const ensureHost = () => {
  if (!host) {
    adoptStyles(CSS); // constructable stylesheet — CSP-safe under style-src 'self'
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

export const Toast = toast; // PascalCase alias for the convention
