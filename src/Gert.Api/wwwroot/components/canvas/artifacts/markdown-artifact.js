// components/canvas/artifacts/markdown-artifact.js — md viewer: sanitized DOM
// (render) + raw source. Both wrappers are always built; the parent
// .art-doc[data-mode] toggles which one shows (see areas/artifacts.css).
// Security F4: bodies go through renderMarkdown, which never interprets raw HTML.
import van from "van";
import { component } from "../../../lib/component.js";
import { renderMarkdown } from "../../../lib/markdown.js";
import { highlight } from "../../../lib/highlight.js";

const { div } = van.tags;

export const MarkdownArtifact = component({
  name: "markdown-artifact",
  css: `
    .md-render{padding:22px 24px 40px; max-width:680px;}
    .md-render h1{font-family:var(--display); font-weight:600; font-size:23px; letter-spacing:-.01em; margin:0 0 4px;}
    .md-render h2{font-family:var(--display); font-weight:600; font-size:16px; margin:22px 0 8px; padding-bottom:5px; border-bottom:1px solid var(--line);}
    .md-render h3{font-family:var(--display); font-weight:600; font-size:14px; margin:18px 0 6px;}
    .md-render p{font-size:14px; line-height:1.66; color:var(--ink); margin:0 0 12px;}
    .md-render ul,.md-render ol{margin:0 0 13px; padding-left:20px;}
    .md-render li{font-size:14px; line-height:1.6; margin-bottom:5px;}
    .md-render li::marker{color:var(--coral);}
    .md-render strong{font-weight:700;} .md-render em{font-style:italic; color:var(--ink-2);}
    .md-render a{color:var(--coral-deep); text-decoration:underline;}
    .md-render code{font-family:var(--mono); font-size:12px; background:var(--surface-2); border:1px solid var(--line); border-radius:4px; padding:1px 5px;}
    .md-render pre{background:var(--code-bg); color:var(--code-fg); border-radius:8px; padding:12px 14px; margin:0 0 14px; overflow-x:auto;}
    .md-render pre code{background:none; border:none; padding:0; color:inherit; font-size:12px;}
    .md-render blockquote{border-left:3px solid var(--coral); background:var(--coral-soft); border-radius:0 8px 8px 0; padding:10px 14px; margin:0 0 14px; font-style:italic; color:var(--coral-deep);}
    .md-render table{border-collapse:collapse; width:100%; margin:0 0 14px; font-size:12.5px;}
    .md-render th{text-align:left; font-family:var(--mono); font-size:10px; letter-spacing:.04em; text-transform:uppercase; color:var(--ink-3); padding:7px 10px; border-bottom:1.5px solid var(--line);}
    .md-render td{padding:8px 10px; border-bottom:1px solid var(--line); vertical-align:top;}
    .md-render tr:hover td{background:var(--surface-2);}
  `,
  view: ({ artifact } = {}) =>
  div(
    { class: "art-body" },
    // re-renders when streamed content changes; renderMarkdown is XSS-safe.
    div({ class: "render" }, () => {
      const host = div({ class: "md-render" });
      host.append(renderMarkdown(artifact.content || ""));
      return host;
    }),
    div(
      { class: "source" },
      // tinted raw source (headings, fences, links) — highlight() emits DOM
      // nodes from textContent, so this stays XSS-safe.
      div({ class: "source-view" }, () => {
        const host = div();
        host.append(...highlight(artifact.content || "", "md"));
        return host;
      }),
    ),
  ),
});
