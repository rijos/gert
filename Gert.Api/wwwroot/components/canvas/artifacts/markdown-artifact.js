// components/canvas/artifacts/markdown-artifact.js — md viewer: sanitized DOM
// (render) + raw source. Both wrappers are always built; the parent
// .art-doc[data-mode] toggles which one shows (see areas/artifacts.css).
// Security F4: bodies go through renderMarkdown, which never interprets raw HTML.
import van from "van";
import { renderMarkdown } from "../../../lib/markdown.js";

const { div } = van.tags;

export const MarkdownArtifact = ({ artifact } = {}) =>
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
      div({ class: "source-view" }, () => artifact.content || ""),
    ),
  );
