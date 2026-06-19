// The activity block: reasoning + tool calls (everything the model did before
// answering) condensed into one collapsible chip with a past-tense summary line.
// Open while the turn streams, collapses once the answer lands; a user click
// overrides that automatic state and is never fought.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { ToolCard } from "./tool-card.js";
import type { Message as MessageRow, ToolCard as ToolCardRow } from "../../state/chat.js";
import type { ToolKind } from "../../services/wire.js";

const { div, span, button } = van.tags;

// Per-kind past-tense summary line; exhaustive over ToolKind.
const SUMMARY: Record<ToolKind, (c: ToolCardRow) => string> = {
  rag: () => "Searched your docs",
  search: () => "Searched the web",
  sandbox: () => "Ran code",
  todo: () => "Updated todos",
  clock: () => "Checked the time",
  make_artifact: (c) => "Created " + (c.query || "a file"),
  edit_artifact: (c) => "Edited " + (c.query || "a file"),
  read_artifact: (c) => "Read " + (c.query || "a file"),
  ask_user: () => "Asked you",
  fetch: () => "Fetched a page",
  memory: () => "Saved a memory",
  sub_agent: () => "Ran a sub-agent",
};

const activitySummary = (m: MessageRow) => {
  const parts: string[] = [];
  if (m.reasoning) parts.push("Thought");
  const seen = new Set<string>();
  for (const c of m.tools) {
    const s = SUMMARY[c.kind](c);
    if (!seen.has(s)) {
      seen.add(s);
      parts.push(s);
    }
  }
  const shown = parts.slice(0, 3);
  const more = parts.length - shown.length;
  return shown.join(" - ") + (more > 0 ? ` - +${more}` : "");
};

export const Activity = component({
  name: "activity",
  css: `
    .activity {
      margin: 0 0 12px;
      border: 1px solid var(--line);
      border-radius: var(--r);
      background: var(--surface);
      box-shadow: var(--lift);
      overflow: hidden;
    }
    .activity.none {
      display: none;
    }
    .act-head {
      display: flex;
      align-items: center;
      gap: 9px;
      width: 100%;
      padding: var(--sp-2) var(--sp-3);
      background: none;
      border: none;
      cursor: pointer;
      font-family: var(--sans);
      color: var(--ink-2);
      font-size: var(--fs-sm);
      font-weight: 600;
      text-align: left;
    }
    .act-head .act-mark {
      color: var(--coral-deep);
      flex: none;
    }
    .act-sum {
      min-width: 0;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .act-live {
      color: var(--coral-deep);
      font-weight: 700;
    }
    .act-chev {
      margin-left: auto;
      color: var(--ink-3);
      flex: none;
      transition: transform var(--t-slow) var(--ease);
    }
    .activity.open .act-chev {
      transform: rotate(180deg);
    }
    .act-body {
      padding: 2px 12px 12px;
    }
    .act-body.hide {
      display: none;
    }
    .act-think {
      margin: 0 1px 12px;
      font-size: var(--fs-sm);
      line-height: var(--lh-ui);
      color: var(--ink-2);
      white-space: pre-wrap;
      overflow-wrap: anywhere;
    }

    .toolzone {
      margin: 0;
    }
  `,
  setup: (m: MessageRow) => {
    const userOpen = van.state<boolean | null>(null); // null -> follow the stream (open while live)
    const isOpen = () => userOpen.val ?? !!m.streaming;
    const present = () => !!(m.reasoning || m.tools.length);
    return { userOpen, isOpen, present };
  },
  // hidden-via-class rather than conditional render: the block keeps fine-
  // grained inner bindings (summary text / thinking text / tool list) instead
  // of rebuilding every card on each reasoning token.
  view: ({ userOpen, isOpen, present }, m: MessageRow) =>
    div({ class: () => "activity" + (isOpen() ? " open" : "") + (present() ? "" : " none") },
      button({ class: "act-head", "aria-expanded": () => String(isOpen()), onclick: () => (userOpen.val = !isOpen()) },
        Icon("sparkle", { size: 15, class: "act-mark", strokeWidth: 1.7 }),
        span({ class: "act-sum" }, () => activitySummary(m)),
        () => (m.streaming ? span({ class: "act-live" }, "...") : span()),
        Icon("chevron", { size: 15, class: "act-chev", strokeWidth: 2.2 }),
      ),
      div({ class: () => "act-body" + (isOpen() ? "" : " hide") },
        // the model's scratchpad is not markdown - raw pre-wrapped text
        () => (m.reasoning ? div({ class: "act-think" }, m.reasoning) : div()),
        () => m.tools.length
          ? div({ class: "toolzone" }, ...m.tools.map((c) => ToolCard(c)))
          : div(),
      ),
    ),
});
