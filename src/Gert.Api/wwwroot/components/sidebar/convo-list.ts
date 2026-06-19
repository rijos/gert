// components/sidebar/convo-list.js - date-grouped rows: Today / Yesterday,
// then one header per calendar date (the date description for anything older
// than yesterday). Binds reactively to state/chat.conversations (van-x list).
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { ConvoItem } from "./convo-item.js";
import { groupOf, rank } from "./convo-list.helpers.js";
import * as chat from "../../state/chat.js";
import type { Conversation } from "../../state/chat.js";
import { t } from "../../lib/i18n.js";

const { div } = van.tags;

export const ConvoList = component({
  name: "convo-list",
  css: `
    .convos {
      flex: 1;
      min-height: 0;
      overflow-y: auto;
      padding: 4px 0 12px;
    }

    .convo-group {
      padding: 14px 22px 6px;
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      letter-spacing: .08em;
      text-transform: uppercase;
      color: var(--ink-3);
    }

    .branch {
      margin: 0 12px;
      display: flex;
      flex-direction: column;
      gap: 1px;
    }
  `,
  view: () =>
    div(
      { class: "convos" },
      // re-render the grouped structure when the list changes
      () => {
        const groups = new Map<string, Conversation[]>(); // insertion order = list order (newest first)
        for (const c of chat.conversations) {
          const g = groupOf(c.updated_at);
          if (!groups.has(g)) groups.set(g, []);
          // the line above guarantees the key exists, so get() is present.
          groups.get(g)!.push(c);
        }
        const names = [...groups.keys()].sort((a, b) => rank(a) - rank(b));
        return div(
          ...names.map((g) =>
            div(
              div({ class: "convo-group" }, t(g)),
              // g comes from groups.keys(), so get() is present here too.
              div({ class: "branch" }, ...groups.get(g)!.map((c) => ConvoItem(c))),
            ),
          ),
        );
      },
    ),
});
