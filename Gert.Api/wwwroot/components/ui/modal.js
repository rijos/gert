// components/ui/modal.js — centered modal over a scrim. Imperative open/close
// helpers since modals are transient and not part of a persistent store.
import van from "van";
import { Button } from "./button.js";

const { div, h2, p } = van.tags;

// Modal({ title, body, confirmLabel, onConfirm }) -> mounts itself, returns close().
export const Modal = ({ title, body, confirmLabel = "OK", onConfirm } = {}) => {
  const close = () => host.remove();
  const host = div(
    { class: "modal-scrim", onclick: (e) => e.target === host && close() },
    div(
      { class: "modal" },
      title ? h2(title) : null,
      typeof body === "string" ? p(body) : body,
      div(
        { class: "modal-acts" },
        Button({ label: "Cancel", variant: "secondary", onclick: close }),
        Button({
          label: confirmLabel,
          onclick: () => {
            onConfirm && onConfirm();
            close();
          },
        }),
      ),
    ),
  );
  van.add(document.body, host);
  return close;
};
