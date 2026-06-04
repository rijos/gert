// components/sidebar/convo-list.js — grouped (Today/Yesterday/Earlier)
// git-graph branches. Binds reactively to state/chat.conversations (van-x list).
import van from "van";
import { list } from "van-x";
import { ConvoItem } from "./convo-item.js";
import * as chat from "../../state/chat.js";

const { div } = van.tags;

const DAY = 86_400_000;

const groupOf = (updatedAt) => {
  if (!updatedAt) return "Earlier";
  const age = Date.now() - new Date(updatedAt).getTime();
  if (age < DAY) return "Today";
  if (age < 2 * DAY) return "Yesterday";
  return "Earlier";
};

const ORDER = ["Today", "Yesterday", "Earlier"];

export const ConvoList = () =>
  div(
    { class: "convos" },
    // re-render the grouped structure when the list changes
    () => {
      const groups = { Today: [], Yesterday: [], Earlier: [] };
      for (const c of chat.conversations) groups[groupOf(c.updated_at)].push(c);
      return div(
        ...ORDER.filter((g) => groups[g].length).map((g) =>
          div(
            div({ class: "convo-group" }, g),
            div({ class: "branch" }, ...groups[g].map((c) => ConvoItem(c))),
          ),
        ),
      );
    },
  );
