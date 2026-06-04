// components/main/message.js — one user/bot message (role header + body).
// Bot bodies are rendered with the sanitizing markdown renderer (security F4):
// no raw HTML is interpreted, javascript:/data: URLs are stripped, external
// links get rel="noopener noreferrer" target="_blank". User text is a plain
// text node (escaped by construction). The streaming caret + tool cards +
// footnotes are reactive renders of the message's van-x state.
import van from "van";
import { renderMarkdown } from "../../lib/markdown.js";
import { ToolCard } from "./tool-card.js";
import { Citation } from "./citation.js";
import { Caret } from "./caret.js";
import { Footnotes } from "./footnotes.js";

const { div, span } = van.tags;

// Replace [n] markers in text nodes with Citation chips when a matching
// citation ordinal exists. Walks only text nodes, so it never re-parses HTML.
const injectCitations = (root, citations) => {
  if (!citations.length) return;
  const byOrdinal = new Map(citations.map((c) => [String(c.ordinal), c]));
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const targets = [];
  let n;
  while ((n = walker.nextNode())) if (/\[\d+\]/.test(n.nodeValue)) targets.push(n);
  for (const node of targets) {
    const parts = node.nodeValue.split(/(\[\d+\])/);
    const frag = document.createDocumentFragment();
    for (const part of parts) {
      const m = /^\[(\d+)\]$/.exec(part);
      const cit = m && byOrdinal.get(m[1]);
      if (cit) frag.append(Citation({ ordinal: cit.ordinal, label: cit.label }));
      else if (part) frag.append(document.createTextNode(part));
    }
    node.replaceWith(frag);
  }
};

const RoleHeader = (isBot) =>
  div(
    { class: "role " + (isBot ? "gert" : "you") },
    span({ class: "rb" }, "G"),
    span({ class: "rl" }, isBot ? "Gert" : "You"),
  );

// `m` is a reactive message: { role, text, streaming, tools, citations }
export const Message = (m) => {
  const isBot = m.role === "assistant";

  if (!isBot) {
    return div(
      { class: "msg user" },
      RoleHeader(false),
      div({ class: "body" }, () => m.text),
    );
  }

  return div(
    { class: "msg bot" },
    RoleHeader(true),
    // tool cards (git-graph). re-renders as cards arrive.
    () =>
      m.tools.length
        ? div({ class: "toolzone" }, ...m.tools.map((c) => ToolCard(c)))
        : div(),
    // sanitized markdown body + streaming caret
    () => {
      const body = div({ class: "body" });
      body.append(renderMarkdown(m.text));
      injectCitations(body, m.citations);
      if (m.streaming) body.append(Caret());
      return body;
    },
    // footnotes
    Footnotes(m.citations),
  );
};
