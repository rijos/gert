// components/sidebar/convo-item.js — one .convo row with its git-graph node.
// The title ellipsises when too wide; a trash button reveals on row hover.
import van from "van";
import { Icon } from "../../icons/icons.js";
import * as chat from "../../state/chat.js";
import * as svc from "../../services/conversations.js";
import { navigate } from "../../lib/router.js";
import { attempt } from "../../lib/action.js";

const { div, span, button } = van.tags;

export const ConvoItem = (convo) =>
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
  );
