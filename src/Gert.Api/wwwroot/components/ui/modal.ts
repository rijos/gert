// components/ui/modal.js - generic centered modal over native <dialog>. Imperative
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

const { dialog: dialogTag, h2, p, button, div } = van.tags;

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

let modalSeq = 0;

export const Modal = component({
  name: "modal",
  css: `
    /* Backdrop replaces .modal-scrim */
    dialog::backdrop {
      background: var(--scrim);
      backdrop-filter: blur(2px);
    }

    /* Dialog element replaces .modal */
    dialog {
      margin: auto;
      background: var(--surface);
      border: 1px solid var(--line);
      border-radius: var(--r);
      box-shadow: var(--shadow-modal);
      padding: 0;
      width: min(440px, calc(100vw - 32px));
      max-width: unset;
      max-height: 85vh;
      overflow-y: auto;
      position: relative;
    }

    /* content lives in the card so the padding belongs to the card, not the dialog - then a
       click whose target is the dialog itself can only be a backdrop click (see close handler) */
    dialog .modal-card {
      padding: var(--sp-5);
    }

    /* entry animation; the backwards fill shows the from-frame before the first paint */
    dialog:modal {
      animation: rise var(--t-slow) var(--ease) backwards;
    }

    dialog h2 {
      font-family: var(--display);
      font-size: var(--fs-lg);
      font-weight: 600;
      margin-bottom: 6px;
    }

    /* a URL/destination shown for confirmation: break long tokens so it stays
       inside the dialog, and scroll once even the wrapped form is too tall */
    dialog .modal-url {
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

    dialog .modal-close {
      position: absolute;
      top: 12px;
      right: 12px;
    }

    /* the footer reads as a region - same hairline language as .t-docs-wrap */
    dialog .modal-acts {
      display: flex;
      gap: 8px;
      justify-content: flex-end;
      margin-top: 18px;
      padding-top: 14px;
      border-top: 1px solid var(--line);
    }

    dialog .ver {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      color: var(--ink-3);
      letter-spacing: .02em;
      margin-top: 14px;
    }

    @keyframes rise {
      from { opacity: 0; transform: scale(.95) translateY(6px); }
      to { opacity: 1; transform: none; }
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
    const dialogId = "modal-" + ++modalSeq;
    const titleId = "modal-title-" + modalSeq;

    // A native <dialog> takes its accessible name from aria-labelledby/aria-label, NOT from a
    // contained <h2> - point it at the heading (or label it generically) so AT announces it
    // named (WCAG 4.1.2).
    const d = dialogTag({
      id: dialogId,
      ...(title ? { "aria-labelledby": titleId } : { "aria-label": t("Dialog") }),
    }) as HTMLDialogElement;

    // Every close path (button, backdrop, Esc) ends in the native `close` event, so that is the
    // single place the node is removed - an Esc-dismissed dialog can't leak. showModal() restores
    // focus to the opener as part of closing (WCAG 2.4.3).
    const close = () => d.close();
    d.addEventListener("close", () => d.remove());

    // X in the top-right, only when closable.
    const closeBtn: ChildDom | null = closable
      ? button(
          { type: "button", class: "ghost modal-close", title: t("Close"), "aria-label": t("Close"), onclick: close },
          Icon("close", { size: 15, strokeWidth: 2.2 }),
        )
      : null;

    // Footer: the caller's custom actions, else the default Cancel + confirm pair.
    let footer: ChildDom;
    if (actions) {
      footer = typeof actions === "function" ? actions(close) : actions;
    } else {
      const confirmAndClose = () => {
        onConfirm?.();
        close();
      };
      footer = div(
        { class: "modal-acts" },
        Button({ label: t("Cancel"), variant: "secondary", onclick: close }),
        Button({ label: confirmLabel, onclick: confirmAndClose }),
      );
      // Enter in a single-line field confirms (the removed <form> used to give submit-on-Enter
      // for free). Skip a nested popover's own input - e.g. a Dropdown search, which handles its
      // own Enter - and an in-progress IME composition.
      d.addEventListener("keydown", (e) => {
        if (
          e.key === "Enter" &&
          !e.isComposing &&
          e.target instanceof HTMLInputElement &&
          !e.target.closest("[popover]")
        ) {
          e.preventDefault();
          confirmAndClose();
        }
      });
    }

    van.add(
      d,
      div(
        { class: "modal-card" },
        closeBtn,
        title ? h2({ id: titleId }, title) : null,
        typeof body === "string" ? p(body) : body,
        footer,
      ),
    );

    // A click whose target is the dialog itself landed on the backdrop, not the content.
    d.addEventListener("click", (e) => {
      if (dismissable && e.target === d) close();
    });
    // Esc fires `cancel` before closing; block it when the modal isn't dismissable.
    d.addEventListener("cancel", (e) => {
      if (!dismissable) e.preventDefault();
    });

    // showModal() must run after the node is in the document.
    van.add(document.body, d);
    d.showModal();

    return close;
  },
});
