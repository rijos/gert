// components/main/message.js — one user/bot message (role header + body).
// Bot bodies are rendered with the sanitizing markdown renderer (security F4):
// no raw HTML is interpreted, javascript:/data: URLs are stripped, external
// links get rel="noopener noreferrer" target="_blank". User text is a plain
// text node (escaped by construction). The streaming caret + tool cards +
// footnotes are reactive renders of the message's van-x state.
//
// The streaming caret, inline citation chips, and footnote list are trivial
// single-use leaves of a message, so they live here rather than in their own files.
import van from "van";
import { component } from "../../lib/component.js";
import { renderMarkdown } from "../../lib/markdown.js";
import { ToolCard } from "./tool-card.js";

const { div, span } = van.tags;

// inline [n] superscript marker
const Citation = ({ ordinal, label } = {}) =>
  span({ class: "cite", title: label || "" }, String(ordinal));

// streaming typewriter caret
const Caret = () => span({ class: "caret" });

// footnote list under a bot message (citations is a van-x reactive list)
const Footnotes = (citations) =>
  () =>
    citations.length
      ? div(
          { class: "footnotes" },
          ...citations.map((c) =>
            div(
              { class: "fn" },
              span({ class: "n" }, String(c.ordinal)),
              span(span({ class: "src" }, c.label || "")),
            ),
          ),
        )
      : div();

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

export const Message = component({
  name: "message",
  css: `
    .msg{margin-bottom:30px; animation:rise .5s cubic-bezier(.2,.8,.2,1) backwards;}

    .role{display:flex; align-items:center; gap:9px; margin-bottom:9px;}
    .role .rb{width:23px; height:23px; border-radius:7px; display:grid; place-items:center; font-family:var(--display); font-size:12px; font-weight:600;}
    .role.you .rb{background:var(--surface-2); color:var(--ink-soft); border:1px solid var(--line-strong);}
    .role.gert .rb{background:linear-gradient(140deg,var(--accent),var(--accent-deep)); color:var(--on-accent);}
    .role .rl{font-weight:700; font-size:12px; letter-spacing:.02em;}
    .role.gert .rl{color:var(--accent-deep);}

    .msg.user .body{background:var(--inset); border:1px solid var(--line); border-left:2.5px solid var(--line-strong); border-radius:var(--r); padding:13px 16px; color:var(--ink); font-size:14.5px;}
    .msg.bot .body{font-size:15px; line-height:1.62; color:var(--ink);}
    .msg.bot .body p{margin-bottom:12px;}
    .msg.bot .body strong{font-weight:700;}
    .msg.bot .body em{font-style:italic;}
    .msg.bot .body a{color:var(--accent-deep); text-decoration:underline;}
    .msg.bot .body code{font-family:var(--mono); font-size:12.5px; background:var(--surface-2); padding:1.5px 5px; border-radius:5px; border:1px solid var(--line);}
    .msg.bot .body ul{margin:0 0 12px; padding-left:20px;}
    .msg.bot .body li{margin-bottom:5px;}

    .cite{font-family:var(--mono); font-size:9.5px; vertical-align:super; color:var(--on-accent); background:var(--accent); border-radius:4px; padding:1px 4px; margin:0 1px; cursor:pointer; line-height:1; transition:.12s;}
    .cite:hover{background:var(--accent-deep);}
    .footnotes{margin-top:14px; padding-top:11px; border-top:1px dashed var(--line-strong); display:flex; flex-direction:column; gap:6px;}
    .fn{font-family:var(--mono); font-size:11px; color:var(--ink-soft); display:flex; gap:8px; align-items:baseline;}
    .fn .n{color:var(--accent); font-weight:500;}
    .fn .src{color:var(--ink);}

    .caret{display:inline-block; width:8px; height:16px; background:var(--accent); margin-left:2px; vertical-align:-2px; animation:blink 1.05s steps(2,start) infinite; border-radius:1px;}

    /* toolzone: git-graph spine that holds the tool-call cards */
    .toolzone{position:relative; padding-left:26px; margin:14px 0 16px;}
    .toolzone::before{content:""; position:absolute; left:10px; top:-4px; bottom:-4px; width:1.5px; background:var(--line-strong);}
  `,
  // `m` is a reactive message: { role, text, streaming, tools, citations }
  view: (m) => {
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
  },
});
