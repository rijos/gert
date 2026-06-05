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

const { div, span, a, button } = van.tags;

// inline [n] superscript marker (exported for the component-unit harness)
export const Citation = ({ ordinal, label } = {}) =>
  span({ class: "cite", title: label || "" }, String(ordinal));

// streaming typewriter caret
const Caret = () => span({ class: "caret" });

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

// hover copy button for fenced code blocks: wraps each <pre> so the button can
// pin to the top-right corner without scrolling along with overflowing code.
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
    wrap.append(pre, btn);
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

    /* long unbroken tokens (URLs, hashes) wrap instead of overflowing the thread */
    .msg .body{min-width:0; overflow-wrap:anywhere;}
    .msg.user .body{background:var(--inset); border:1px solid var(--line); border-left:2.5px solid var(--line-strong); border-radius:var(--r); padding:13px 16px; color:var(--ink); font-size:14.5px;}
    .msg.bot .body{font-size:15px; line-height:1.62; color:var(--ink);}
    .msg.bot .body p{margin-bottom:12px;}
    .msg.bot .body strong{font-weight:700;}
    .msg.bot .body em{font-style:italic;}
    .msg.bot .body a{color:var(--accent-deep); text-decoration:underline;}
    .msg.bot .body code{font-family:var(--mono); font-size:12.5px; background:var(--surface-2); padding:1.5px 5px; border-radius:5px; border:1px solid var(--line);}
    /* fenced code blocks: scroll horizontally inside the bubble (same look as
       .md-render pre in the canvas) instead of stretching the thread */
    .msg.bot .body pre{background:var(--code-bg); color:var(--code-fg); border-radius:8px; padding:12px 14px; margin:0; overflow-x:auto;}
    .msg.bot .body pre code{background:none; border:none; padding:0; color:inherit; font-size:12px; overflow-wrap:normal;}
    /* the wrapper anchors the hover copy button so it doesn't scroll with the code */
    .codewrap{position:relative; margin:0 0 12px;}
    .copy-btn{position:absolute; top:7px; right:7px; display:grid; place-items:center; width:26px; height:26px; border-radius:7px; border:1px solid color-mix(in srgb, var(--code-fg) 22%, transparent); background:color-mix(in srgb, var(--code-bg) 82%, var(--code-fg)); color:var(--code-fg); cursor:pointer; opacity:0; transition:.15s;}
    .codewrap:hover .copy-btn,.copy-btn:focus-visible{opacity:1;}
    .copy-btn:hover{border-color:var(--accent); color:#fff;}
    .copy-btn .ck{display:none;}
    .copy-btn.copied svg{display:none;}
    .copy-btn.copied .ck{display:block; color:#9fc487;}
    .msg.bot .body ul,.msg.bot .body ol{margin:0 0 12px; padding-left:24px;}
    .msg.bot .body li{margin-bottom:5px;}

    .cite{font-family:var(--mono); font-size:10px; vertical-align:super; color:var(--accent-deep); background:var(--surface-2); border:1px solid var(--line); border-radius:5px; padding:1px 5px; margin:0 2px; cursor:pointer; line-height:1; transition:.12s;}
    .cite:hover{background:var(--accent-soft); border-color:var(--accent);}

    /* sources card: collapsed header bar + expandable source list */
    .sources{margin-top:14px; border:1px solid var(--line-strong); border-radius:13px; background:var(--surface); overflow:hidden;}
    .s-head{display:flex; align-items:center; gap:10px; width:100%; padding:11px 14px; background:none; border:none; cursor:pointer; font-family:var(--sans); color:var(--ink); font-size:13.5px; font-weight:700; text-align:left;}
    .s-head .s-mark{color:var(--accent); flex:none;}
    .s-count{font-family:var(--mono); font-size:11px; font-weight:500; color:var(--accent-deep); background:var(--surface-2); border:1px solid var(--line); border-radius:6px; padding:1.5px 7px;}
    .s-stack{display:flex; margin-left:3px;}
    .s-stack .s-avatar{margin-left:-7px; box-shadow:0 0 0 2px var(--surface);}
    .s-stack .s-avatar:first-child{margin-left:0;}
    .s-chev{margin-left:auto; color:var(--ink-faint); flex:none; transition:transform .2s;}
    .sources.open .s-chev{transform:rotate(180deg);}
    .s-avatar{width:22px; height:22px; border-radius:7px; display:grid; place-items:center; font-size:11px; font-weight:600; color:var(--ink); border:1px solid var(--line); flex:none;}
    .s-list{padding:2px 8px 10px;}
    .s-row{display:flex; align-items:center; gap:11px; padding:8px 9px; border-radius:9px; text-decoration:none; color:inherit; transition:.13s;}
    .s-row .s-avatar{width:27px; height:27px; font-size:12.5px; border-radius:8px;}
    a.s-row:hover{background:var(--inset);}
    .s-ord{font-family:var(--mono); font-size:11.5px; color:var(--accent); min-width:14px; text-align:right; flex:none;}
    .s-meta{min-width:0;}
    .s-title{font-size:13.5px; font-weight:600; color:var(--ink); white-space:nowrap; overflow:hidden; text-overflow:ellipsis;}
    .s-domain{font-size:11.5px; color:var(--ink-faint); margin-top:1.5px;}
    .s-ext{margin-left:auto; color:var(--ink-faint); opacity:0; flex:none; transition:.13s;}
    a.s-row:hover .s-ext{opacity:1;}

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
        decorateCodeBlocks(body);
        if (m.streaming) body.append(Caret());
        return body;
      },
      // sources card
      Sources(m.citations),
    );
  },
});
