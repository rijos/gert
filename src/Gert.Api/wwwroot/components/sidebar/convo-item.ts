// One .convo row: title, plus move/trash buttons that reveal on row hover.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { Modal } from "../ui/modal.js";
import { Dropdown } from "../ui/dropdown.js";
import * as chat from "../../state/chat.js";
import type { Conversation } from "../../state/chat.js";
import * as svc from "../../services/conversations.js";
import { navigate } from "../../lib/router.js";
import { attempt } from "../../lib/action.js";
import { t } from "../../lib/i18n.js";

const { div, span, button, p } = van.tags;

// "Move to project..." modal; the current project is excluded from the picker.
const promptMove = (convo: Conversation) => {
  const target = van.state("");
  const choices = chat.projects
    .filter((pr) => pr.id !== chat.activeProjectId.val)
    .map((pr) => ({ value: pr.id, label: pr.name }));
  if (!choices.length) return;
  const wasActive = chat.activeId.val === convo.id;
  Modal({
    title: t("Move chat"),
    body: div(
      p({ class: "sub" }, `${t("Move chat")}: "${convo.title || t("Untitled")}"`),
      div({ class: "field" }, Dropdown({ items: choices, value: target, placeholder: "Project" })),
    ),
    confirmLabel: t("Move"),
    onConfirm: async () => {
      if (!target.val) return;
      const ok = await attempt(async () => {
        await svc.move(convo.id, target.val);
        return true;
      }, "Couldn't move this chat");
      if (ok && wasActive) navigate("/");
    },
  });
};

export const ConvoItem = component({
  name: "convo-item",
  css: `
    .convo {
      position: relative;
      padding: var(--sp-2) 10px;
      border-radius: var(--r-sm);
      cursor: pointer;
      display: flex;
      align-items: center;
      color: var(--ink-2);
      transition: var(--t-fast);
    }

    /* the row's open action is a real <button> (keyboard-operable, WCAG 2.1.1):
       flex:1 + min-width:0 lets the title shrink so text-overflow:ellipsis fires.
       Reset the button chrome so it reads as the row text it replaces. */
    .convo .t {
      flex: 1;
      min-width: 0;
      font-family: inherit;
      font-size: var(--fs-md);
      font-weight: 500;
      text-align: left;
      color: inherit;
      background: none;
      border: none;
      padding: 0;
      cursor: pointer;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    /* stretch the open-button's hit area over the whole row, so a click anywhere
       (not only the text) opens it; move/trash sit above it via z-index. */
    .convo .t::after { content: ""; position: absolute; inset: 0; }

    .convo .trash {
      margin-left: 6px;
      position: relative;
      z-index: 1;
    }     /* base .trash (hidden, hover-reveal) is a shared primitive */

    .convo .mv {
      margin-left: 6px;
      opacity: 0;
      position: relative;
      z-index: 1;
      display: flex;
      align-items: center;
      justify-content: center;
      width: 24px;
      height: 24px;
      flex: none;
      background: none;
      border: none;
      border-radius: 5px;
      color: var(--ink-3);
      cursor: pointer;
      padding: 0;
      transition: var(--t-fast);
    }

    .convo .mv:hover {
      color: var(--coral-deep);
      background: var(--coral-soft);
    }

    .convo:hover {
      background: var(--surface-2);
      color: var(--ink);
    }

    .convo:hover .trash {
      opacity: 1;
    }

    .convo:hover .mv {
      opacity: 1;
    }

    /* active = one signal: the soft accent fill + accent text. The old git-graph
       node + edge bar were extra signals the cleaner list doesn't need. */
    .convo.active {
      background: var(--chat-on);
      color: var(--coral-deep);
      font-weight: 600;
    }
  `,
  setup: (convo: Conversation) => {
    // Navigate only - ChatPage opens the thread for its route. Calling
    // svc.open() here too would race a SECOND open from the route render
    // (activeId hasn't flipped yet), and the loser's detach() could kill the
    // winner's in-flight resume of a streaming turn.
    const open = () => navigate("/c/" + convo.id);

    const move = (e: Event) => {
      e.stopPropagation();
      promptMove(convo);
    };

    // stop the row's onclick from opening the thread we're deleting.
    const remove = async (e: Event) => {
      e.stopPropagation();
      const wasActive = chat.activeId.val === convo.id;
      // navigate only on success (section 8): attempt() returns undefined on
      // failure - a failed delete must not bounce the user home.
      const ok = await attempt(async () => {
        await svc.remove(convo.id);
        return true;
      }, "Couldn't delete this chat");
      if (ok && wasActive) navigate("/");
    };

    return { open, move, remove };
  },
  view: ({ open, move, remove }, convo: Conversation) =>
    // role=listitem pairs with the convo-list's role=list (WCAG 1.3.1); the open action is a
    // button carrying aria-current="page" for the active thread.
    div({ class: () => "convo" + (chat.activeId.val === convo.id ? " active" : ""), "data-id": convo.id, role: "listitem" },
      button(
        { class: "t", onclick: open, "aria-current": () => (chat.activeId.val === convo.id ? "page" : "false") },
        () => convo.title || t("Untitled"),
      ),
      // hidden with a single project - there is nowhere to move to
      () => chat.projects.length > 1
        ? button({ class: "mv", title: t("Move to project..."), "aria-label": () => `${t("Move to project...")} ${convo.title || t("Untitled")}`, onclick: move }, Icon("external", { size: 13, strokeWidth: 2 }))
        : span(),
      button({ class: "trash", title: t("Delete chat"), "aria-label": () => `${t("Delete chat")}: ${convo.title || t("Untitled")}`, onclick: remove }, Icon("trash", { size: 14, strokeWidth: 2 })),
    ),
});
