// components/sidebar/convo-list.js - date-grouped rows: Today / Yesterday,
// then one header per calendar date (the date description for anything older
// than yesterday). Binds reactively to state/chat.conversations (van-x list).
import van from "van";
import { component } from "../../lib/component.js";
import { ConvoItem } from "./convo-item.js";
import * as chat from "../../state/chat.js";
import { t } from "../../lib/i18n.js";

const { div } = van.tags;

const DAY = 86_400_000;

const startOfDay = (d) =>
  new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();

// Calendar-date grouping (not age): a chat from 11 pm yesterday is
// "Yesterday" at 8 am today, whatever the hour gap says.
const groupOf = (updatedAt) => {
  const t = new Date(updatedAt || NaN);
  if (Number.isNaN(t.getTime())) return "Earlier";
  const now = new Date();
  const today = startOfDay(now);
  const day = startOfDay(t);
  if (day >= today) return "Today";
  if (day >= today - DAY) return "Yesterday";
  return t.toLocaleDateString(undefined, {
    month: "long",
    day: "numeric",
    ...(t.getFullYear() !== now.getFullYear() ? { year: "numeric" } : {}),
  });
};

// Today, Yesterday, then date groups in list order (newest first); the
// undated fallback sinks to the bottom.
const rank = (g) =>
  g === "Today" ? 0 : g === "Yesterday" ? 1 : g === "Earlier" ? 3 : 2;

export const ConvoList = component({
  name: "convo-list",
  css: `
    .convos{flex:1; min-height:0; overflow-y:auto; padding:4px 0 12px;}
    .convo-group{padding:14px 22px 6px; font-family:var(--mono); font-size:var(--fs-2xs); letter-spacing:.08em; text-transform:uppercase; color:var(--ink-3);}
    .branch{margin:0 12px; display:flex; flex-direction:column; gap:1px;}
  `,
  view: () =>
    div(
      { class: "convos" },
      // re-render the grouped structure when the list changes
      () => {
        const groups = new Map(); // insertion order = list order (newest first)
        for (const c of chat.conversations) {
          const g = groupOf(c.updated_at);
          if (!groups.has(g)) groups.set(g, []);
          groups.get(g).push(c);
        }
        const names = [...groups.keys()].sort((a, b) => rank(a) - rank(b));
        return div(
          ...names.map((g) =>
            div(
              div({ class: "convo-group" }, t(g)),
              div({ class: "branch" }, ...groups.get(g).map((c) => ConvoItem(c))),
            ),
          ),
        );
      },
    ),
});
