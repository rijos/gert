// components/ui/modal.js — generic centered modal over a scrim. Imperative
// open/close since modals are transient, not part of a persistent store.
//
// Modal({ title, body, confirmLabel?, onConfirm?, actions?, dismissable? })
//   title       — heading (omit for none)
//   body        — string (wrapped in <p>) or any node
//   actions     — escape hatch: a node, or fn(close) => node, replacing the
//                 default footer. Use for non-confirm layouts.
//   confirmLabel/onConfirm — default footer: Cancel + a primary button.
//   dismissable — backdrop click / Esc closes (default true).
// Returns close().
import van from "van";
import { Button } from "./button.js";

const { div, h2, p } = van.tags;

export const Modal = ({
  title,
  body,
  confirmLabel = "OK",
  onConfirm,
  actions,
  dismissable = true,
} = {}) => {
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

  const host = div(
    { class: "modal-scrim", onclick: (e) => dismissable && e.target === host && close() },
    div(
      { class: "modal" },
      title ? h2(title) : null,
      typeof body === "string" ? p(body) : body,
      footer,
    ),
  );
  van.add(document.body, host);
  document.addEventListener("keydown", onKey);
  return close;
};
