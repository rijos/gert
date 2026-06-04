// components/sidebar/convo-item.js — one .convo row with its git-graph node.
import van from "van";
import * as chat from "../../state/chat.js";
import * as svc from "../../services/conversations.js";
import { navigate } from "../../lib/router.js";

const { div, span } = van.tags;

export const ConvoItem = (convo) =>
  div(
    {
      class: () => "convo" + (chat.activeId.val === convo.id ? " active" : ""),
      onclick: () => {
        svc.open(convo.id).catch(() => {});
        navigate("/c/" + convo.id);
      },
    },
    span({ class: "node" }),
    span({ class: "t" }, () => convo.title || "Untitled"),
  );
