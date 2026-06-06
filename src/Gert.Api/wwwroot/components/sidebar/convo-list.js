// components/sidebar/convo-list.js — grouped (Today/Yesterday/Earlier)
// git-graph branches. Binds reactively to state/chat.conversations (van-x list).
import van from "van";
import { list } from "van-x";
import { component } from "../../lib/component.js";
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

export const ConvoList = component({
  name: "convo-list",
  css: `
    .convos{flex:1; min-height:0; overflow-y:auto; padding:4px 0 12px;}
    .convo-group{padding:14px 22px 6px; font-family:var(--mono); font-size:10px; letter-spacing:.09em; text-transform:uppercase; color:var(--ink-3);}
    .branch{position:relative; padding-left:30px; margin:0 12px;}
    .branch::before{content:""; position:absolute; left:13px; top:0; bottom:0; width:1.5px; background:var(--line);}
  `,
  view: () =>
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
    ),
});
