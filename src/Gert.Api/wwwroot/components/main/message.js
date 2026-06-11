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
import { Icon } from "../../icons/icons.js";
import { ToolCard } from "./tool-card.js";
import * as auth from "../../state/auth.js";

const { div, span, a, button, img } = van.tags;

// inline [n] superscript marker (exported for the component-unit harness)
export const Citation = ({ ordinal, label } = {}) =>
  span({ class: "cite", title: label || "" }, String(ordinal));

// streaming typewriter caret
const Caret = () => span({ class: "caret" });

// "working" pulse — the busy indicator while the turn is live but no answer text
// has begun yet: the pre-first-token wait, reasoning, and tool execution between
// rounds. Once answer text streams, the caret takes over (see the body render).
const Working = () =>
  span(
    { class: "working", role: "status", "aria-label": "Working" },
    span({ class: "wdot" }),
    span({ class: "wdot" }),
    span({ class: "wdot" }),
  );

// --- thinking block (collapsible, mirrors the sources card) ------------------
// Collapsed by default; the header appears the moment reasoning text arrives
// and the body live-appends while the model thinks. Raw pre-wrapped text — the
// model's scratchpad is not markdown.
const Thinking = (m) => {
  const open = van.state(false);
  return () => {
    if (!m.reasoning) return div();
    return div(
      { class: () => "thinking" + (open.val ? " open" : "") },
      button(
        {
          class: "t-head",
          "aria-expanded": () => String(open.val),
          onclick: () => (open.val = !open.val),
        },
        Icon("brain", { size: 16, class: "t-mark", strokeWidth: 1.7 }),
        span({ class: "t-label" }, "Thinking"),
        () =>
          m.streaming && !m.text
            ? span({ class: "t-live" }, "…")
            : span(),
        Icon("chevron", { size: 15, class: "t-chev", strokeWidth: 2.2 }),
      ),
      () => (open.val ? div({ class: "t-body" }, m.reasoning) : div()),
    );
  };
};

// --- per-message generation stats ("312 tok · 41 tok/s") ---------------------
const Meta = (m) => () => {
  if (m.streaming || m.tokenCount == null) return div();
  const tps =
    m.durationMs > 0 ? Math.round(m.tokenCount / (m.durationMs / 1000)) : null;
  return div(
    { class: "msg-meta" },
    `${m.tokenCount} tok` + (tps != null ? ` · ${tps} tok/s` : ""),
  );
};

// --- sources card (the collapsible footnote redesign) ------------------------
// Only http(s) locators become links — same URL stance as the markdown
// renderer; anything else (document pages) renders as a plain row.
const domainOf = (locator) => {
  if (!/^https?:\/\//i.test(locator || "")) return null;
  try {
    return new URL(locator).hostname.replace(/^www\./, "");
  } catch {
    return null;
  }
};

// brand-ish letter avatar: first letter of the registrable domain, tinted
// deterministically from the domain so the same source always matches.
const avatarHue = (key) => {
  let h = 0;
  for (const ch of key) h = (h * 31 + ch.codePointAt(0)) % 360;
  return h;
};

const Avatar = (c) => {
  const domain = domainOf(c.locator);
  const key = domain || c.label || "?";
  const parts = (domain || "").split(".");
  const core = parts.length > 1 ? parts[parts.length - 2] : key;
  return span(
    {
      class: "s-avatar",
      style: `background:color-mix(in srgb, hsl(${avatarHue(key)} 60% 50%) 22%, var(--surface-2))`,
    },
    (core[0] || "?").toUpperCase(),
  );
};

const SourceRow = (c) => {
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

// collapsed: icon · "Sources" · count · avatar stack (unique domains) · chevron.
// `open` lives outside the binding so re-renders (late citations) keep the state.
const Sources = (citations) => {
  const open = van.state(false);
  return () => {
    if (!citations.length) return div();
    const seen = new Set();
    const stack = citations.filter((c) => {
      const key = domainOf(c.locator) || c.label;
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });
    return div(
      { class: () => "sources" + (open.val ? " open" : "") },
      button(
        {
          class: "s-head",
          "aria-expanded": () => String(open.val),
          onclick: () => (open.val = !open.val),
        },
        Icon("websearch", { size: 17, class: "s-mark", strokeWidth: 1.7 }),
        span({ class: "s-label" }, "Sources"),
        span({ class: "s-count" }, String(citations.length)),
        span({ class: "s-stack" }, ...stack.slice(0, 4).map((c) => Avatar(c))),
        Icon("chevron", { size: 15, class: "s-chev", strokeWidth: 2.2 }),
      ),
      () =>
        open.val
          ? div({ class: "s-list" }, ...citations.map((c) => SourceRow(c)))
          : div(),
    );
  };
};

// code-block chrome: wraps each <pre> with a header strip carrying the fence's
// language label (data-lang, set by lib/markdown.js) and the copy button — the
// button sits in the strip, so it neither floats over code nor scrolls with it,
// and stays reachable on touch devices (no hover needed).
const decorateCodeBlocks = (body) => {
  for (const pre of body.querySelectorAll("pre")) {
    const wrap = div({ class: "codewrap" });
    pre.replaceWith(wrap);
    const btn = button(
      {
        class: "copy-btn",
        title: "Copy code",
        onclick: () => {
          const text = pre.querySelector("code")?.textContent ?? "";
          (navigator.clipboard?.writeText(text) ?? Promise.reject()).catch(
            () => {
              // non-secure contexts: fall back to the selection API
              const ta = document.createElement("textarea");
              ta.value = text;
              document.body.appendChild(ta);
              ta.select();
              document.execCommand("copy");
              ta.remove();
            },
          );
          btn.classList.add("copied");
          setTimeout(() => btn.classList.remove("copied"), 1200);
        },
      },
      Icon("copy", { size: 13, strokeWidth: 2 }),
      Icon("check", { size: 13, strokeWidth: 2.4, class: "ck" }),
    );
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
    // user rows wear the user's initial (same derivation as the sidebar chip)
    span({ class: "rb" }, isBot ? "G" : () => auth.user.val?.avatar || "Y"),
    span({ class: "rl" }, isBot ? "Gert" : "You"),
  );

export const Message = component({
  name: "message",
  css: `
    .msg{margin-bottom:30px; animation:rise .5s var(--ease) backwards;}

    .role{display:flex; align-items:center; gap:9px; margin-bottom:6px;}
    .role .rb{width:23px; height:23px; border-radius:7px; display:grid; place-items:center; font-family:var(--display); font-size:var(--fs-sm); font-weight:600;}
    .role.you .rb{background:var(--surface-2); color:var(--ink-2); border:1px solid var(--line);}
    .role.gert .rb{background:linear-gradient(135deg,var(--coral),var(--coral-2)); color:var(--on-coral);}
    /* the speaker name shares the mono meta voice (msg-meta, menu-h) */
    .role .rl{font-family:var(--mono); font-weight:600; font-size:var(--fs-2xs); letter-spacing:.08em; text-transform:uppercase; color:var(--ink-2);}
    .role.gert .rl{color:var(--coral-deep);}

    /* long unbroken tokens (URLs, hashes) wrap instead of overflowing the thread */
    .msg .body{min-width:0; overflow-wrap:anywhere;}
    .msg.user .body{background:var(--bubble); border:1px solid var(--line); border-radius:var(--r); padding:13px 16px; color:var(--ink); font-size:var(--fs-base);}
    /* a received page shouldn't float — but the author's line breaks must hold */
    .msg.user .att-text{white-space:pre-wrap;}
    /* pasted images on a user message: bounded thumbnails above the text */
    .msg.user .att-grid{display:flex; flex-wrap:wrap; gap:8px;}
    .msg.user .att-grid + .att-text{margin-top:10px;}
    .msg.user .att-img{max-width:280px; max-height:220px; border-radius:10px; border:1px solid var(--line); cursor:zoom-in; display:block;}
    .msg.bot .body{font-size:var(--fs-base); line-height:var(--lh-reading); color:var(--ink); max-width:68ch;}
    .msg.bot .body p{margin-bottom:12px;}
    .msg.bot .body strong{font-weight:700;}
    .msg.bot .body em{font-style:italic;}
    .msg.bot .body a{color:var(--coral-deep); text-decoration:underline;}
    .msg.bot .body code{font-family:var(--mono); font-size:var(--fs-sm); background:var(--surface-2); padding:1.5px 5px; border-radius:5px; border:1px solid var(--line);}
    /* fenced code blocks: scroll horizontally inside the bubble (same look as
       .md-render pre in the canvas) instead of stretching the thread */
    .msg.bot .body pre{background:var(--code-bg); color:var(--code-fg); border-radius:var(--r-sm); padding:13px 16px; margin:0; overflow-x:auto;}
    .msg.bot .body pre code{background:none; border:none; padding:0; color:inherit; font-size:var(--fs-sm); overflow-wrap:normal;}
    /* code chrome: header strip (language label + copy button) over the pre;
       the wrap owns the radius so strip + code read as one printed block */
    .codewrap{margin:0 0 12px; border-radius:var(--r-sm); background:var(--code-bg); overflow:hidden;}
    .codewrap pre{border-radius:0;}
    .code-head{display:flex; align-items:center; justify-content:space-between; padding:5px 7px 5px 16px; border-bottom:1px solid color-mix(in srgb, var(--code-fg) 12%, transparent);}
    .code-lang{font-family:var(--mono); font-size:var(--fs-2xs); letter-spacing:.08em; text-transform:uppercase; color:color-mix(in srgb, var(--code-fg) 72%, transparent);}
    /* always visible at low emphasis — hover isn't required (touch devices) */
    .copy-btn{display:grid; place-items:center; width:26px; height:26px; border-radius:7px; border:1px solid color-mix(in srgb, var(--code-fg) 22%, transparent); background:none; color:var(--code-fg); cursor:pointer; opacity:.55; transition:var(--t-fast); flex:none;}
    .codewrap:hover .copy-btn,.copy-btn:focus-visible{opacity:1;}
    .copy-btn:hover{border-color:var(--coral-2); color:var(--coral-2); opacity:1;}
    .copy-btn .ck{display:none;}
    .copy-btn.copied svg{display:none;}
    .copy-btn.copied .ck{display:block; color:var(--code-attr);}
    .msg.bot .body ul,.msg.bot .body ol{margin:0 0 12px; padding-left:24px;}
    .msg.bot .body li{margin-bottom:5px;}
    /* GFM tables (mirrors .md-render's table skin in the canvas) */
    .msg.bot .body table{border-collapse:collapse; width:100%; margin:0 0 14px; font-size:var(--fs-md);}
    .msg.bot .body th{text-align:left; font-family:var(--mono); font-size:var(--fs-2xs); letter-spacing:.04em; text-transform:uppercase; color:var(--ink-3); padding:7px 10px; border-bottom:1.5px solid var(--line);}
    .msg.bot .body td{padding:8px 10px; border-bottom:1px solid var(--line); vertical-align:top;}
    .msg.bot .body tr:hover td{background:var(--surface-2);}

    .cite{font-family:var(--mono); font-size:var(--fs-2xs); vertical-align:super; color:var(--coral-deep); background:var(--surface-2); border:1px solid var(--line); border-radius:5px; padding:1px 5px; margin:0 2px; cursor:pointer; line-height:1; transition:var(--t-fast);}
    .cite:hover{background:var(--coral-soft); border-color:var(--coral);}

    /* sources card: collapsed header bar + expandable source list */
    .sources{margin-top:14px; border:1px solid var(--line); border-radius:var(--r); background:var(--surface); overflow:hidden;}
    .s-head{display:flex; align-items:center; gap:10px; width:100%; padding:11px 14px; background:none; border:none; cursor:pointer; font-family:var(--sans); color:var(--ink); font-size:var(--fs-md); font-weight:700; text-align:left;}
    .s-head .s-mark{color:var(--coral-deep); flex:none;}
    .s-count{font-family:var(--mono); font-size:var(--fs-xs); font-weight:500; color:var(--coral-deep); background:var(--surface-2); border:1px solid var(--line); border-radius:var(--r-xs); padding:1.5px 7px;}
    .s-stack{display:flex; margin-left:3px;}
    .s-stack .s-avatar{margin-left:-7px; box-shadow:0 0 0 2px var(--surface);}
    .s-stack .s-avatar:first-child{margin-left:0;}
    .s-chev{margin-left:auto; color:var(--ink-3); flex:none; transition:transform var(--t-slow) var(--ease);}
    .sources.open .s-chev{transform:rotate(180deg);}
    .s-avatar{width:22px; height:22px; border-radius:7px; display:grid; place-items:center; font-size:var(--fs-xs); font-weight:600; color:var(--ink); border:1px solid var(--line); flex:none;}
    .s-list{padding:2px 8px 10px;}
    .s-row{display:flex; align-items:center; gap:11px; padding:var(--sp-2) 9px; border-radius:var(--r-sm); text-decoration:none; color:inherit; transition:var(--t-fast);}
    .s-row .s-avatar{width:27px; height:27px; font-size:var(--fs-sm); border-radius:8px;}
    a.s-row:hover{background:var(--surface-2);}
    .s-ord{font-family:var(--mono); font-size:var(--fs-xs); color:var(--coral-deep); min-width:14px; text-align:right; flex:none;}
    .s-meta{min-width:0;}
    .s-title{font-size:var(--fs-md); font-weight:600; color:var(--ink); white-space:nowrap; overflow:hidden; text-overflow:ellipsis;}
    .s-domain{font-size:var(--fs-xs); color:var(--ink-3); margin-top:1.5px;}
    .s-ext{margin-left:auto; color:var(--ink-3); opacity:0; flex:none; transition:var(--t-fast);}
    a.s-row:hover .s-ext{opacity:1;}

    .caret{display:inline-block; width:8px; height:16px; background:var(--coral); margin-left:2px; vertical-align:-2px; animation:blink 1.05s steps(2,start) infinite; border-radius:1px;}

    /* "working" pulse: three breathing dots while the turn is live before any
       answer text (waiting / thinking / tool execution between rounds) */
    .working{display:inline-flex; gap:5px; align-items:center; height:18px;}
    .working .wdot{width:6px; height:6px; border-radius:50%; background:var(--coral); opacity:.3; animation:wpulse 1.1s ease-in-out infinite;}
    .working .wdot:nth-child(2){animation-delay:.16s;}
    .working .wdot:nth-child(3){animation-delay:.32s;}
    @keyframes wpulse{0%,80%,100%{opacity:.28; transform:scale(.8);} 40%{opacity:1; transform:scale(1);}}

    /* user-stopped turn: quiet meta line under the partial text */
    .stopped{margin-top:8px; font-size:var(--fs-sm); color:var(--ink-3); font-style:italic;}

    /* thinking block: collapsible scratchpad above the answer (raised surface) */
    .thinking{margin:0 0 12px; border:1px solid var(--line); border-radius:var(--r); background:var(--surface); box-shadow:var(--lift); overflow:hidden;}
    .t-head{display:flex; align-items:center; gap:9px; width:100%; padding:var(--sp-2) var(--sp-3); background:none; border:none; cursor:pointer; font-family:var(--sans); color:var(--ink-2); font-size:var(--fs-sm); font-weight:700; text-align:left;}
    .t-head .t-mark{color:var(--ink-3); flex:none;}
    .t-live{color:var(--coral-deep); font-weight:700;}
    .t-chev{margin-left:auto; color:var(--ink-3); flex:none; transition:transform var(--t-slow) var(--ease);}
    .thinking.open .t-chev{transform:rotate(180deg);}
    .t-body{padding:2px 13px 11px; font-size:var(--fs-sm); line-height:var(--lh-ui); color:var(--ink-2); white-space:pre-wrap; overflow-wrap:anywhere;}

    /* generation stats under the answer */
    .msg-meta{margin-top:7px; font-family:var(--mono); font-size:var(--fs-xs); color:var(--ink-3);}

    /* artifact chip: a named fence collapsed to a clickable file card */
    .artifact-chip{display:flex; align-items:center; gap:8px; width:fit-content; max-width:100%; margin:0 0 12px; padding:8px 13px; background:var(--surface); border:1px solid var(--line); border-radius:10px; box-shadow:var(--lift); cursor:pointer; font-family:var(--mono); font-size:var(--fs-sm); color:var(--ink); transition:var(--t-fast);}
    .artifact-chip:hover{border-color:var(--coral); background:var(--coral-soft); transform:translateY(-1px);}
    .artifact-chip svg{color:var(--coral); flex:none;}
    .artifact-chip .ac-name{overflow:hidden; text-overflow:ellipsis; white-space:nowrap;}
    .artifact-chip .ac-hint{color:var(--ink-3); font-family:var(--sans); font-size:var(--fs-xs); flex:none;}

    /* toolzone: git-graph spine that holds the tool-call cards */
    .toolzone{position:relative; padding-left:26px; margin:14px 0 16px;}
    .toolzone::before{content:""; position:absolute; left:10px; top:-4px; bottom:-4px; width:1.5px; background:var(--line);}
  `,
  // `m` is a reactive message: { role, text, streaming, tools, citations }
  view: (m) => {
    const isBot = m.role === "assistant";

    if (!isBot) {
      return div(
        { class: "msg user" },
        RoleHeader(false),
        div(
          { class: "body" },
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
                      // can't be window.open'd directly). create → use →
                      // revoke (§11): the timeout outlives the new tab's load,
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
      RoleHeader(true),
      // thinking scratchpad (collapsed; live-fills while the model reasons)
      Thinking(m),
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
        decorateCodeBlocks(body);
        // Live turn: the busy pulse shows whenever the model is working —
        // thinking, running tools, or waiting between rounds — and the caret
        // takes over only while answer text is actively streaming.
        if (m.streaming) body.append(m.working ? Working() : Caret());
        return body;
      },
      // user-stopped marker (server-confirmed `cancelled` terminal)
      () => (m.cancelled ? div({ class: "stopped" }, "Stopped") : div()),
      // generation stats ("312 tok · 41 tok/s")
      Meta(m),
      // sources card
      Sources(m.citations),
    );
  },
});
