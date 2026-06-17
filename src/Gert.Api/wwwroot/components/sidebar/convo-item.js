// components/sidebar/convo-item.js - one .convo row.
// The title ellipsises when too wide; move/trash buttons reveal on row hover.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { Modal } from "../ui/modal.js";
import { Dropdown } from "../ui/dropdown.js";
import * as chat from "../../state/chat.js";
import * as svc from "../../services/conversations.js";
import { navigate } from "../../lib/router.js";
import { attempt } from "../../lib/action.js";
import { t } from "../../lib/i18n.js";

const { div, span, button, p } = van.tags;

// "Move to project..." - a modal with a destination picker (the current project
// excluded; nothing to do with one project).
const promptMove = (convo) => {
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
    .convo{position:relative; padding:var(--sp-2) 10px; border-radius:var(--r-sm); cursor:pointer; display:flex; align-items:center; color:var(--ink-2); transition:var(--t-fast);}
    /* flex:1 + min-width:0 lets the title shrink so text-overflow:ellipsis fires */
    .convo .t{flex:1; min-width:0; font-size:var(--fs-md); font-weight:500; white-space:nowrap; overflow:hidden; text-overflow:ellipsis;}
    .convo .trash{margin-left:6px;}     /* base .trash (hidden, hover-reveal) is a shared primitive */
    .convo .mv{margin-left:6px; opacity:0; display:flex; align-items:center; justify-content:center; width:22px; height:22px; flex:none; background:none; border:none; border-radius:5px; color:var(--ink-3); cursor:pointer; padding:0; transition:var(--t-fast);}
    .convo .mv:hover{color:var(--coral-deep); background:var(--coral-soft);}
    .convo:hover{background:var(--surface-2); color:var(--ink);}
    .convo:hover .trash{opacity:1;}
    .convo:hover .mv{opacity:1;}
    /* active = one signal: the soft accent fill + accent text. The old git-graph
       node + edge bar were extra signals the cleaner list doesn't need. */
    .convo.active{background:var(--chat-on); color:var(--coral-deep); font-weight:600;}
  `,
  view: (convo) =>
  div(
    {
      class: () => "convo" + (chat.activeId.val === convo.id ? " active" : ""),
      "data-id": convo.id,
      // Navigate only - ChatPage opens the thread for its route. Calling
      // svc.open() here too would race a SECOND open from the route render
      // (activeId hasn't flipped yet), and the loser's detach() could kill the
      // winner's in-flight resume of a streaming turn.
      onclick: () => navigate("/c/" + convo.id),
    },
    span({ class: "t" }, () => convo.title || t("Untitled")),
    // hidden with a single project - there is nowhere to move to
    () =>
      chat.projects.length > 1
        ? button(
            {
              class: "mv",
              title: t("Move to project..."),
              onclick: (e) => {
                e.stopPropagation();
                promptMove(convo);
              },
            },
            Icon("external", { size: 13, strokeWidth: 2 }),
          )
        : span(),
    button(
      {
        class: "trash",
        title: t("Delete chat"),
        // stop the row's onclick from opening the thread we're deleting.
        onclick: async (e) => {
          e.stopPropagation();
          const wasActive = chat.activeId.val === convo.id;
          // navigate only on success (section 8): attempt() returns undefined on
          // failure - a failed delete must not bounce the user home.
          const ok = await attempt(async () => {
            await svc.remove(convo.id);
            return true;
          }, "Couldn't delete this chat");
          if (ok && wasActive) navigate("/");
        },
      },
      Icon("trash", { size: 14, strokeWidth: 2 }),
    ),
  ),
});
