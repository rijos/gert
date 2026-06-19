// components/main/sources.js - the sources card under a bot answer: a collapsed
// header bar (icon - "Sources" - count - avatar stack of unique domains -
// chevron) that expands to the full source list. Only http(s) locators become
// links - same URL stance as the markdown renderer; anything else (document
// pages) renders as a plain row.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { Avatar } from "./avatar.js";
import { domainOf } from "./message.helpers.js";
import type { Citation as CitationRow } from "../../state/chat.js";

const { div, span, a, button } = van.tags;

const SourceRow = (c: CitationRow) => {
  const domain = domainOf(c.locator);
  const tag = domain ? a : div;
  return tag(
    {
      class: "s-row",
      ...(domain && {
        href: c.locator,
        target: "_blank",
        rel: "noopener noreferrer",
      }),
    },
    span({ class: "s-ord" }, String(c.ordinal)),
    Avatar(c),
    div(
      { class: "s-meta" },
      div({ class: "s-title" }, c.label || ""),
      div({ class: "s-domain" }, domain || c.locator || "document"),
    ),
    domain ? Icon("external", { size: 15, class: "s-ext", strokeWidth: 2 }) : null,
  );
};

export const Sources = component({
  name: "sources",
  css: `
    .sources {
      margin-top: 14px;
      border: 1px solid var(--line);
      border-radius: var(--r);
      background: var(--surface);
      overflow: hidden;
    }
    .s-head {
      display: flex;
      align-items: center;
      gap: 10px;
      width: 100%;
      padding: 11px 14px;
      background: none;
      border: none;
      cursor: pointer;
      font-family: var(--sans);
      color: var(--ink);
      font-size: var(--fs-md);
      font-weight: 700;
      text-align: left;
    }
    .s-head .s-mark {
      color: var(--coral-deep);
      flex: none;
    }
    .s-count {
      font-family: var(--mono);
      font-size: var(--fs-xs);
      font-weight: 500;
      color: var(--coral-deep);
      background: var(--surface-2);
      border: 1px solid var(--line);
      border-radius: var(--r-xs);
      padding: 1.5px 7px;
    }
    .s-stack {
      display: flex;
      margin-left: 3px;
    }
    .s-stack .s-avatar {
      margin-left: -7px;
      box-shadow: 0 0 0 2px var(--surface);
    }
    .s-stack .s-avatar:first-child {
      margin-left: 0;
    }
    .s-chev {
      margin-left: auto;
      color: var(--ink-3);
      flex: none;
      transition: transform var(--t-slow) var(--ease);
    }
    .sources.open .s-chev {
      transform: rotate(180deg);
    }
    .s-list {
      padding: 2px 8px 10px;
    }
    .s-row {
      display: flex;
      align-items: center;
      gap: 11px;
      padding: var(--sp-2) 9px;
      border-radius: var(--r-sm);
      text-decoration: none;
      color: inherit;
      transition: var(--t-fast);
    }
    .s-row .s-avatar {
      width: 27px;
      height: 27px;
      font-size: var(--fs-sm);
      border-radius: 8px;
    }
    a.s-row:hover {
      background: var(--surface-2);
    }
    .s-ord {
      font-family: var(--mono);
      font-size: var(--fs-xs);
      color: var(--coral-deep);
      min-width: 14px;
      text-align: right;
      flex: none;
    }
    .s-meta {
      min-width: 0;
    }
    .s-title {
      font-size: var(--fs-md);
      font-weight: 600;
      color: var(--ink);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .s-domain {
      font-size: var(--fs-xs);
      color: var(--ink-3);
      margin-top: 1.5px;
    }
    .s-ext {
      margin-left: auto;
      color: var(--ink-3);
      opacity: 0;
      flex: none;
      transition: var(--t-fast);
    }
    a.s-row:hover .s-ext {
      opacity: 1;
    }
  `,
  // `open` lives in setup (a plain van.state) so re-renders driven by late
  // citations keep the expanded/collapsed state across the binding rebuild.
  // (setup takes the call arg so the factory infers the call signature, even
  // though only `view` reads `citations`.)
  setup: (_citations: CitationRow[]) => {
    const open = van.state(false);
    return { open };
  },
  // The whole card is a binding so a late citation re-renders it.
  view: ({ open }, citations: CitationRow[]) => () => {
    if (!citations.length) return div();
    const seen = new Set<string>();
    const stack = citations.filter((c) => {
      const key = domainOf(c.locator) || c.label;
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });
    return div({ class: () => "sources" + (open.val ? " open" : "") },
      button({ class: "s-head", "aria-expanded": () => String(open.val), onclick: () => (open.val = !open.val) },
        Icon("websearch", { size: 17, class: "s-mark", strokeWidth: 1.7 }),
        span({ class: "s-label" }, "Sources"),
        span({ class: "s-count" }, String(citations.length)),
        span({ class: "s-stack" }, ...stack.slice(0, 4).map((c) => Avatar(c))),
        Icon("chevron", { size: 15, class: "s-chev", strokeWidth: 2.2 }),
      ),
      () => open.val
        ? div({ class: "s-list" }, ...citations.map((c) => SourceRow(c)))
        : div(),
    );
  },
});
