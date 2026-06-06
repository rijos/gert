// components/sidebar/convo-item.js — one .convo row with its git-graph node.
// The title ellipsises when too wide; a trash button reveals on row hover.
import van from "van";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import * as chat from "../../state/chat.js";
import * as svc from "../../services/conversations.js";
import { navigate } from "../../lib/router.js";
import { attempt } from "../../lib/action.js";

const { div, span, button } = van.tags;

export const ConvoItem = component({
  name: "convo-item",
  css: `
    .convo{position:relative; padding:8px 10px 8px 8px; border-radius:var(--r-sm); cursor:pointer; display:flex; align-items:center; color:var(--ink-2); transition:.14s;}
    .convo .node{position:absolute; left:-21.5px; top:50%; transform:translateY(-50%); width:9px; height:9px; border-radius:50%; background:var(--side-bg); border:1.5px solid var(--line); transition:.14s;}
    /* flex:1 + min-width:0 lets the title shrink so text-overflow:ellipsis fires */
    .convo .t{flex:1; min-width:0; font-size:13px; font-weight:500; white-space:nowrap; overflow:hidden; text-overflow:ellipsis;}
    .convo .trash{margin-left:6px;}     /* base .trash (hidden, hover-reveal) is a shared primitive */
    .convo:hover{background:var(--surface-2); color:var(--ink);}
    .convo:hover .node{border-color:var(--ink-3);}
    .convo:hover .trash{opacity:1;}
    .convo.active{background:var(--chat-on); color:var(--coral-deep); font-weight:600; box-shadow:inset 0 0 0 1px var(--coral-line);}
    /* 3px coral bar pinned to the active row's left edge */
    .convo.active::before{content:""; position:absolute; left:0; top:6px; bottom:6px; width:3px; border-radius:0 2px 2px 0; background:var(--coral);}
    .convo.active .node{background:var(--coral); border-color:var(--coral); box-shadow:0 0 0 3px var(--coral-soft);}
  `,
  view: (convo) =>
  div(
    {
      class: () => "convo" + (chat.activeId.val === convo.id ? " active" : ""),
      "data-id": convo.id,
      onclick: () => {
        attempt(() => svc.open(convo.id), "Couldn't open this chat");
        navigate("/c/" + convo.id);
      },
    },
    span({ class: "node" }),
    span({ class: "t" }, () => convo.title || "Untitled"),
    button(
      {
        class: "trash",
        title: "Delete chat",
        // stop the row's onclick from opening the thread we're deleting.
        onclick: (e) => {
          e.stopPropagation();
          const wasActive = chat.activeId.val === convo.id;
          attempt(() => svc.remove(convo.id), "Couldn't delete this chat");
          if (wasActive) navigate("/");
        },
      },
      Icon("trash", { size: 14, strokeWidth: 2 }),
    ),
  ),
});
