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
    .convo{position:relative; padding:8px 10px 8px 0; border-radius:var(--r-sm); cursor:pointer; display:flex; align-items:center; color:var(--ink-soft); transition:.14s;}
    .convo .node{position:absolute; left:-21.5px; top:50%; transform:translateY(-50%); width:9px; height:9px; border-radius:50%; background:var(--paper); border:1.5px solid var(--line-strong); transition:.14s;}
    /* flex:1 + min-width:0 lets the title shrink so text-overflow:ellipsis fires */
    .convo .t{flex:1; min-width:0; font-size:13px; font-weight:500; white-space:nowrap; overflow:hidden; text-overflow:ellipsis;}
    .convo .trash{margin-left:6px;}     /* base .trash (hidden, hover-reveal) is a shared primitive */
    .convo:hover{background:var(--inset); color:var(--ink);}
    .convo:hover .node{border-color:var(--ink-faint);}
    .convo:hover .trash{opacity:1;}
    .convo.active{background:var(--accent-soft); color:var(--accent-deep); font-weight:600;}
    .convo.active .node{background:var(--accent); border-color:var(--accent); box-shadow:0 0 0 3px rgba(191,71,39,.13);}
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
