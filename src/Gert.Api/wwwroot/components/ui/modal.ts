// components/ui/modal.js - generic centered modal over a scrim. Imperative
// open/close since modals are transient, not part of a persistent store.
//
// Modal({ title, body, confirmLabel?, onConfirm?, actions?, dismissable?, closable? })
//   title       - heading (omit for none)
//   body        - string (wrapped in <p>) or any node
//   actions     - escape hatch: a node, or fn(close) => node, replacing the
//                 default footer. Use for non-confirm layouts.
//   confirmLabel/onConfirm - default footer: Cancel + a primary button.
//   dismissable - backdrop click / Esc closes (default true).
//   closable    - show a x in the top-right corner; the caller decides
//                 (default false).
// Returns close().
import van from "/lib/van.js";
import type { ChildDom } from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Button } from "./button.js";
import { Icon } from "../../icons/icons.js";
import { t } from "../../lib/i18n.js";

const { div, h2, p, button } = van.tags;

interface ModalProps {
  title?: ChildDom;
  body?: ChildDom;
  confirmLabel?: string;
  onConfirm?: () => void;
  // a node, or fn(close) => node (typeof check narrows the function branch).
  actions?: Node | ((close: () => void) => ChildDom);
  dismissable?: boolean;
  closable?: boolean;
}

export const Modal = component({
  name: "modal",
  css: `
    .modal-scrim {
      position: fixed;
      inset: 0;
      background: var(--scrim);
      backdrop-filter: blur(2px);
      z-index: 70;
      display: grid;
      place-items: center;
    }

    .modal {
      background: var(--surface);
      border: 1px solid var(--line);
      border-radius: var(--r);
      box-shadow: var(--shadow-modal);
      padding: var(--sp-5);
      width: min(440px,calc(100vw - 32px));
      animation: rise var(--t-slow) var(--ease) backwards;
      position: relative;
    }

    .modal h2 {
      font-family: var(--display);
      font-size: var(--fs-lg);
      font-weight: 600;
      margin-bottom: 6px;
    }

    /* a URL/destination shown for confirmation: break long tokens so it stays
       inside the dialog, and scroll once even the wrapped form is too tall */
    .modal .modal-url {
      font-family: var(--mono);
      font-size: var(--fs-sm);
      line-height: 1.55;
      color: var(--ink);
      background: var(--surface-2);
      border: 1px solid var(--line);
      border-radius: var(--r-sm);
      padding: 9px 11px;
      overflow-wrap: anywhere;
      max-height: 40vh;
      overflow-y: auto;
    }

    .modal .modal-close {
      position: absolute;
      top: 12px;
      right: 12px;
    }

    /* the footer reads as a region - same hairline language as .t-docs-wrap */
    .modal .modal-acts {
      display: flex;
      gap: 8px;
      justify-content: flex-end;
      margin-top: 18px;
      padding-top: 14px;
      border-top: 1px solid var(--line);
    }

    .modal .ver {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      color: var(--ink-3);
      letter-spacing: .02em;
      margin-top: 14px;
    }
  `,
  view: ({
    title,
    body,
    confirmLabel = t("OK"),
    onConfirm,
    actions,
    dismissable = true,
    closable = false,
  }: ModalProps = {}) => {
    const close = () => {
      host.remove();
      document.removeEventListener("keydown", onKey);
    };
    const onKey = (e: KeyboardEvent) => {
      if (dismissable && e.key === "Escape") close();
    };

    const host = div(
      { class: "modal-scrim", onclick: (e: Event) => dismissable && e.target === host && close() },
      div(
        { class: "modal" },
        closable
          ? button({ class: "ghost modal-close", title: t("Close"), onclick: close }, Icon("close", { size: 15, strokeWidth: 2.2 }))
          : null,
        title ? h2(title) : null,
        typeof body === "string" ? p(body) : body,
        // the footer: the caller's custom actions, else the default Cancel + confirm pair
        actions
          ? typeof actions === "function" ? actions(close) : actions
          : div({ class: "modal-acts" },
              Button({ label: t("Cancel"), variant: "secondary", onclick: close }),
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
    document.addEventListener("keydown", onKey);
    return close;
  },
});
