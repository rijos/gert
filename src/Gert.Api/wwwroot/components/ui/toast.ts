// components/ui/toast.js - transient toast notifications.
// toast(message, kind) mounts into a fixed host and auto-dismisses. Imperative
// (not a view-returning component), so it co-locates its CSS via a one-time
// <style> injection when the host is first created - mirroring the component()
// factory's inject-once behaviour.
import van from "/lib/van.js";
import { adoptStyles, css } from "../../lib/component.js";

const { div } = van.tags;

// css tag: minified at runtime, and the release bundler minifies the literal too (so the
// shipped app.js carries no verbose toast CSS).
const CSS = css`
  .toast-host {
    position: fixed;
    bottom: 18px;
    right: 18px;
    z-index: 80;
    display: flex;
    flex-direction: column;
    gap: 8px;
  }

  .toast {
    font-size: var(--fs-md);
    font-weight: 500;
    color: var(--ink);
    background: var(--surface);
    border: 1px solid var(--line);
    border-left: 3px solid var(--coral);
    border-radius: var(--r-sm);
    padding: 10px 14px;
    box-shadow: var(--shadow-toast);
    animation: rise var(--t-slow) var(--ease) backwards;
    max-width: 320px;
  }

  .toast.err {
    border-left-color: var(--brick);
  }

  /* .ok rides the default coral edge - success is the accent, not green */
`;

let host: HTMLDivElement | null = null;
const ensureHost = () => {
  if (!host) {
    adoptStyles(CSS); // constructable stylesheet - CSP-safe under style-src 'self'
    // A polite live region so inserted toasts are announced by assistive tech (WCAG 4.1.3).
    // aria-atomic=false: announce only the toast just added, not the whole stack.
    host = div({ class: "toast-host", role: "status", "aria-live": "polite", "aria-atomic": "false" });
    van.add(document.body, host);
  }
  return host;
};

// kind: "info" | "ok" | "err"
export const toast = (message: string, kind = "info", ms = 3200) => {
  // Errors interrupt (assertive); info/ok ride the host's polite region.
  const el = div(
    { class: "toast" + (kind === "info" ? "" : " " + kind), ...(kind === "err" ? { role: "alert" } : {}) },
    message,
  );
  van.add(ensureHost(), el);

  // Auto-dismiss, but pause the countdown while the pointer is over the toast so a reader has
  // time to finish it (WCAG 2.2.1 - a timing it would otherwise impose with no way to extend).
  let remaining = ms;
  let startedAt = Date.now();
  let timer = setTimeout(() => el.remove(), remaining);
  el.addEventListener("pointerenter", () => {
    clearTimeout(timer);
    remaining -= Date.now() - startedAt;
  });
  el.addEventListener("pointerleave", () => {
    startedAt = Date.now();
    timer = setTimeout(() => el.remove(), Math.max(remaining, 800));
  });
};
