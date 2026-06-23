// One user/bot message. No role headers: the user speaks from a right-aligned
// bubble, Gert in plain reading text - the conversation carries who's who.
// Bot bodies go through the sanitizing markdown renderer (security F4): no raw
// HTML, javascript:/data: URLs stripped, external links get
// rel="noopener noreferrer" target="_blank". User text is a plain (escaped) text
// node. Caret and busy pulse are trivial single-use leaves so they live here;
// the richer pieces (activity dropdown, sources, artifact chips, citation,
// actions) are their own files.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { renderMarkdown } from "../../lib/markdown.js";
import { attachLinkConfirm } from "../../lib/markdown-links.js";
import { copyText } from "../../lib/clipboard.js";
import { Icon } from "../../icons/icons.js";
import { Citation } from "./citation.js";
import { Activity } from "./activity.js";
import { QuestionCard } from "./question-card.js";
import { Sources } from "./sources.js";
import { ArtifactChips } from "./artifact-chips.js";
import { MessageActions } from "./message-actions.js";
import type { Citation as CitationRow, Message as MessageRow } from "../../state/chat.js";
import type { Card } from "./tool-card.helpers.js";

const { div, span, button, img } = van.tags;

const Caret = () => span({ class: "caret" });

// Busy indicator while the turn is live but no answer text has begun: the
// pre-first-token wait, reasoning, and tool execution between rounds. Once answer
// text streams, the caret takes over (see the body render).
const Working = () =>
  span(
    { class: "working", role: "status", "aria-label": "Working" },
    span({ class: "wdot" }),
    span({ class: "wdot" }),
    span({ class: "wdot" }),
  );

// Wraps each <pre> with a header strip (language label + copy button). The
// button lives in the strip so it neither floats over code nor scrolls with it
// and stays reachable on touch devices (no hover).
// While streaming the strip carries an empty slot instead: the body re-renders
// per delta, so a live button would flicker per token and could copy a
// half-written block. The real button lands on the final re-render, same 26px slot.
const decorateCodeBlocks = (body: HTMLElement, streaming: boolean) => {
  for (const pre of body.querySelectorAll("pre")) {
    const wrap = div({ class: "codewrap" });
    pre.replaceWith(wrap);
    let btn: HTMLElement;
    if (streaming) {
      btn = span({ class: "copy-slot" });
    } else {
      btn = button(
        {
          class: "copy-btn",
          title: "Copy code",
          onclick: () => {
            copyText(pre.querySelector("code")?.textContent ?? "");
            btn.classList.add("copied");
            setTimeout(() => btn.classList.remove("copied"), 1200);
          },
        },
        Icon("copy", { size: 13, strokeWidth: 2 }),
        Icon("check", { size: 13, strokeWidth: 2.4, class: "ck" }),
      );
    }
    wrap.append(
      div(
        { class: "code-head" },
        span({ class: "code-lang" }, pre.dataset.lang || "code"),
        btn,
      ),
      pre,
    );
  }
};

// Replace [n] markers in text nodes with Citation chips when a matching
// citation ordinal exists. Walks only text nodes, so it never re-parses HTML.
const injectCitations = (root: HTMLElement, citations: CitationRow[]) => {
  if (!citations.length) return;
  const byOrdinal = new Map(citations.map((c) => [String(c.ordinal), c] as const));
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const targets: Text[] = [];
  let n: Node | null;
  // SHOW_TEXT yields only Text nodes (so `as Text`), whose nodeValue is always a
  // string (so `!`) - both type-only, runtime unchanged.
  while ((n = walker.nextNode())) if (/\[\d+\]/.test(n.nodeValue!)) targets.push(n as Text);
  for (const node of targets) {
    const parts = node.nodeValue!.split(/(\[\d+\])/);
    const frag = document.createDocumentFragment();
    for (const part of parts) {
      const m = /^\[(\d+)\]$/.exec(part);
      // m[1] is the captured digits when the regex matched (the `m &&` guard).
      const cit = m && byOrdinal.get(m[1]!);
      if (cit) frag.append(Citation({ ordinal: cit.ordinal, label: cit.label }));
      else if (part) frag.append(document.createTextNode(part));
    }
    node.replaceWith(frag);
  }
};

// A pending ask_user question is lifted OUT of the activity block (which is
// collapsible and frequently collapsed) so it can never be hidden from the
// user: it renders inline under the activity until the user answers, then folds
// back into its tool card as a record (tool-card.js). ask_user blocks the turn,
// so at most one question is pending at a time.
const PendingQuestions = (m: MessageRow) => () => {
  const pending = (m.tools as Card[]).filter(
    (c) => c.question && !c.question.answered && !c.question.expired,
  );
  return pending.length
    ? div({ class: "pending-q" }, ...pending.map((c) => QuestionCard(c.question!)))
    : div();
};

export const Message = component({
  name: "message",
  css: `
    .msg {
      margin-bottom: 30px;
      animation: rise .5s var(--ease) backwards;
    }

    /* long unbroken tokens (URLs, hashes) wrap instead of overflowing the thread */
    .msg .body {
      min-width: 0;
      overflow-wrap: anywhere;
    }
    /* the user speaks from a right-aligned bubble - no role headers, the
       alignment carries who's who. --lift keeps it readable on manila where
       the bubble tint alone is subtle. */
    .msg.user {
      display: flex;
      justify-content: flex-end;
    }
    .msg.user .body {
      max-width: 85%;
      background: var(--bubble);
      border: 1px solid var(--line);
      border-radius: var(--r);
      padding: 13px 16px;
      color: var(--ink);
      font-size: var(--fs-base);
      box-shadow: var(--lift);
    }
    /* a received page shouldn't float - but the author's line breaks must hold */
    .msg.user .att-text {
      white-space: pre-wrap;
    }
    /* pasted images on a user message: bounded thumbnails above the text */
    .msg.user .att-grid {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
    }
    .msg.user .att-grid + .att-text {
      margin-top: 10px;
    }
    .msg.user .att-img {
      max-width: 280px;
      max-height: 220px;
      border-radius: 10px;
      border: 1px solid var(--line);
      cursor: zoom-in;
      display: block;
    }
    .msg.bot .body {
      font-size: var(--fs-base);
      line-height: var(--lh-reading);
      color: var(--ink);
      max-width: 68ch;
    }
    .msg.bot .body p {
      margin-bottom: 12px;
    }
    .msg.bot .body strong {
      font-weight: 700;
    }
    .msg.bot .body em {
      font-style: italic;
    }
    .msg.bot .body a {
      color: var(--coral-deep);
      text-decoration: underline;
    }
    .msg.bot .body code {
      font-family: var(--mono);
      font-size: var(--fs-sm);
      background: var(--surface-2);
      padding: 1.5px 5px;
      border-radius: 5px;
      border: 1px solid var(--line);
    }
    /* fenced code blocks: scroll horizontally inside the bubble (same look as
       .md-render pre in the canvas) instead of stretching the thread */
    .msg.bot .body pre {
      background: var(--code-bg);
      color: var(--code-fg);
      border-radius: var(--r-sm);
      padding: 13px 16px;
      margin: 0;
      overflow-x: auto;
    }
    .msg.bot .body pre code {
      background: none;
      border: none;
      padding: 0;
      color: inherit;
      font-size: var(--fs-sm);
      overflow-wrap: normal;
    }
    /* code chrome: header strip (language label + copy button) over the pre;
       the wrap owns the radius so strip + code read as one printed block */
    .codewrap {
      margin: 0 0 12px;
      border-radius: var(--r-sm);
      background: var(--code-bg);
      overflow: hidden;
    }
    .codewrap pre {
      border-radius: 0;
    }
    .code-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 5px 7px 5px 16px;
      border-bottom: 1px solid color-mix(in srgb, var(--code-fg) 12%, transparent);
    }
    .code-lang {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      letter-spacing: .08em;
      text-transform: uppercase;
      color: color-mix(in srgb, var(--code-fg) 72%, transparent);
    }
    /* always visible at low emphasis - hover isn't required (touch devices) */
    .copy-btn {
      display: grid;
      place-items: center;
      width: 26px;
      height: 26px;
      border-radius: 7px;
      border: 1px solid color-mix(in srgb, var(--code-fg) 22%, transparent);
      background: none;
      color: var(--code-fg);
      cursor: pointer;
      opacity: .55;
      transition: var(--t-fast);
      flex: none;
    }
    /* streaming placeholder: holds the button's slot so the strip doesn't
       resize when the real button lands at end-of-turn */
    .copy-slot {
      width: 26px;
      height: 26px;
      flex: none;
    }
    .codewrap:hover .copy-btn,.copy-btn:focus-visible {
      opacity: 1;
    }
    .copy-btn:hover {
      border-color: var(--coral-2);
      color: var(--coral-2);
      opacity: 1;
    }
    .copy-btn .ck {
      display: none;
    }
    .copy-btn.copied svg {
      display: none;
    }
    .copy-btn.copied .ck {
      display: block;
      color: var(--code-attr);
    }
    .msg.bot .body ul,.msg.bot .body ol {
      margin: 0 0 12px;
      padding-left: 24px;
    }
    .msg.bot .body li {
      margin-bottom: 5px;
    }
    /* GFM tables (mirrors .md-render's table skin in the canvas) */
    .msg.bot .body table {
      border-collapse: collapse;
      width: 100%;
      margin: 0 0 14px;
      font-size: var(--fs-md);
    }
    .msg.bot .body th {
      text-align: left;
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      letter-spacing: .04em;
      text-transform: uppercase;
      color: var(--ink-3);
      padding: 7px 10px;
      border-bottom: 1.5px solid var(--line);
    }
    .msg.bot .body td {
      padding: 8px 10px;
      border-bottom: 1px solid var(--line);
      vertical-align: top;
    }
    .msg.bot .body tr:hover td {
      background: var(--surface-2);
    }

    .caret {
      display: inline-block;
      width: 8px;
      height: 16px;
      background: var(--coral);
      margin-left: 2px;
      vertical-align: -2px;
      animation: blink 1.05s steps(2,start) infinite;
      border-radius: 1px;
    }

    /* "working" pulse: three breathing dots while the turn is live before any
       answer text (waiting / thinking / tool execution between rounds) */
    .working {
      display: inline-flex;
      gap: 5px;
      align-items: center;
      height: 18px;
    }
    .working .wdot {
      width: 6px;
      height: 6px;
      border-radius: 50%;
      background: var(--coral);
      opacity: .3;
      animation: wpulse 1.1s ease-in-out infinite;
    }
    .working .wdot:nth-child(2) {
      animation-delay: .16s;
    }
    .working .wdot:nth-child(3) {
      animation-delay: .32s;
    }
    @keyframes wpulse {
      0%,80%,100% {
        opacity: .28;
        transform: scale(.8);
      }
      40% {
        opacity: 1;
        transform: scale(1);
      }
    }

    /* a pending ask_user question, promoted out of the (collapsible) activity
       block so it is always visible - separated from the activity above it */
    .pending-q {
      margin: 0 0 14px;
    }

    /* user-stopped turn: quiet meta line under the partial text */
    .stopped {
      margin-top: 8px;
      font-size: var(--fs-sm);
      color: var(--ink-3);
      font-style: italic;
    }
  `,
  setup: (m: MessageRow) => ({ isBot: m.role === "assistant" }),
  view: ({ isBot }, m: MessageRow) => {
    if (!isBot) {
      return div({ class: "msg user" },
        div({ class: "body" },
          // pasted images (our own data URLs, never model output) above the text
          () =>
            m.attachments?.length
              ? div(
                  { class: "att-grid" },
                  ...m.attachments.map((att) =>
                    img({
                      class: "att-img",
                      src: `data:${att.mime_type};base64,${att.data}`,
                      alt: "attached image",
                      // full-size view: blob URL in a new tab (a data: URL
                      // can't be window.open'd directly). create -> use ->
                      // revoke (section 11): the timeout outlives the new tab's load,
                      // and revoking only invalidates the URL, not the
                      // already-loaded document.
                      onclick: () =>
                        fetch(`data:${att.mime_type};base64,${att.data}`)
                          .then((r) => r.blob())
                          .then((b) => {
                            const url = URL.createObjectURL(b);
                            window.open(url, "_blank");
                            setTimeout(() => URL.revokeObjectURL(url), 30_000);
                          })
                          .catch(() => {}),
                    }),
                  ),
                )
              : div(),
          () => (m.text ? div({ class: "att-text" }, m.text) : div()),
        ),
      );
    }

    return div(
      { class: "msg bot" },
      Activity(m),
      PendingQuestions(m),
      () => {
        const body = div({ class: "body" });
        body.append(renderMarkdown(m.text));
        injectCitations(body, m.citations);
        decorateCodeBlocks(body, m.streaming);
        // confirm before any external link leaves the app (Gert Modal, delegated)
        attachLinkConfirm(body);
        // pulse while working, caret only while answer text is actively streaming
        if (m.streaming) body.append(m.working ? Working() : Caret());
        return body;
      },
      // user-stopped marker (server-confirmed `cancelled` terminal)
      () => (m.cancelled ? div({ class: "stopped" }, "Stopped") : div()),
      ArtifactChips(m),
      Sources(m.citations),
      MessageActions(m),
    );
  },
});
