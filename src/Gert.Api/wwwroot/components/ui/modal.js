// components/ui/modal.js — generic centered modal over a scrim. Imperative
// open/close since modals are transient, not part of a persistent store.
//
// Modal({ title, body, confirmLabel?, onConfirm?, actions?, dismissable?, closable? })
//   title       — heading (omit for none)
//   body        — string (wrapped in <p>) or any node
//   actions     — escape hatch: a node, or fn(close) => node, replacing the
//                 default footer. Use for non-confirm layouts.
//   confirmLabel/onConfirm — default footer: Cancel + a primary button.
//   dismissable — backdrop click / Esc closes (default true).
//   closable    — show a ✕ in the top-right corner; the caller decides
//                 (default false).
// Returns close().
import van from "van";
import { component } from "../../lib/component.js";
import { Button } from "./button.js";
import { Icon } from "../../icons/icons.js";

const { div, h2, p, button } = van.tags;

export const Modal = component({
  name: "modal",
  css: `
    .modal-scrim{position:fixed; inset:0; background:var(--scrim); backdrop-filter:blur(2px); z-index:70; display:grid; place-items:center;}
    .modal{background:var(--surface); border:1px solid var(--line); border-radius:var(--r); box-shadow:var(--shadow-modal); padding:var(--sp-5); width:min(440px,calc(100vw - 32px)); animation:rise var(--t-slow) var(--ease) backwards; position:relative;}
    .modal h2{font-family:var(--display); font-size:var(--fs-lg); font-weight:600; margin-bottom:6px;}
    .modal .modal-close{position:absolute; top:12px; right:12px;}
    /* the footer reads as a region — same hairline language as .t-docs-wrap */
    .modal .modal-acts{display:flex; gap:8px; justify-content:flex-end; margin-top:18px; padding-top:14px; border-top:1px solid var(--line);}
    .modal .ver{font-family:var(--mono); font-size:var(--fs-2xs); color:var(--ink-3); letter-spacing:.02em; margin-top:14px;}
  `,
  view: ({
    title,
    body,
    confirmLabel = "OK",
    onConfirm,
    actions,
    dismissable = true,
    closable = false,
  } = {}) => {
    // ── logic ───────────────────────────────────
    const close = () => {
      host.remove();
      document.removeEventListener("keydown", onKey);
    };
    const onKey = (e) => {
      if (dismissable && e.key === "Escape") close();
    };

    const footer = actions
      ? typeof actions === "function"
        ? actions(close)
        : actions
      : div(
          { class: "modal-acts" },
          Button({ label: "Cancel", variant: "secondary", onclick: close }),
          Button({
            label: confirmLabel,
            onclick: () => {
              onConfirm && onConfirm();
              close();
            },
          }),
        );

    // ── content ─────────────────────────────────
    const host = div(
      { class: "modal-scrim", onclick: (e) => dismissable && e.target === host && close() },
      div(
        { class: "modal" },
        closable
          ? button(
              { class: "ghost modal-close", title: "Close", onclick: close },
              Icon("close", { size: 15, strokeWidth: 2.2 }),
            )
          : null,
        title ? h2(title) : null,
        typeof body === "string" ? p(body) : body,
        footer,
      ),
    );
    van.add(document.body, host);
    document.addEventListener("keydown", onKey);
    return close;
  },
});
