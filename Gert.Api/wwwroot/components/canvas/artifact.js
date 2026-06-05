// components/canvas/artifact.js — polymorphic dispatcher: picks a viewer by
// artifact.kind, wraps it with the shared header + Rendered/Source mode state.
// Owns the shared artifact shell CSS (.art-head, .art-body, .source-view, the
// data-mode render/source toggle). The header is a trivial single-use leaf, so
// it lives here as ArtifactHead rather than its own file.
import van from "van";
import { component } from "../../lib/component.js";
import { SegToggle } from "../ui/seg-toggle.js";
import { MarkdownArtifact } from "./artifacts/markdown-artifact.js";
import { HtmlArtifact } from "./artifacts/html-artifact.js";
import { SvgArtifact } from "./artifacts/svg-artifact.js";
import { CodeArtifact } from "./artifacts/code-artifact.js";

const { section, div, span } = van.tags;

const VIEWERS = {
  md: MarkdownArtifact,
  html: HtmlArtifact,
  svg: SvgArtifact,
  py: CodeArtifact,
};

const TYPE_LABEL = { md: "MD", html: "HTML", svg: "SVG", py: "PY" };

// type badge + name + Rendered/Source seg toggle (shared across viewers).
// renderLabel lets html/svg say "Preview" instead of "Rendered". `mode` is the
// van.state owned by Artifact below.
const ArtifactHead = ({ artifact, mode, renderLabel = "Rendered" } = {}) =>
  div(
    { class: "art-head" },
    span({ class: "atype " + artifact.kind }, TYPE_LABEL[artifact.kind] || "?"),
    // filename is a van text node — XSS-safe; CSS adds unicode-bidi:isolate.
    span({ class: "aname" }, artifact.name || "untitled"),
    artifact.kind === "py"
      ? span(
          { class: "gen" },
          span({ class: "gd" }),
          (artifact.problems?.length || 0) + " problems",
        )
      : SegToggle({
          options: [
            { value: "render", label: renderLabel },
            { value: "source", label: "Source" },
          ],
          value: () => mode.val,
          onSelect: (v) => (mode.val = v),
        }),
  );

export const Artifact = component({
  name: "artifact",
  css: `
    .art-head{display:flex; align-items:center; gap:9px; padding:11px 13px; border-bottom:1px solid var(--line); flex:none;}
    .art-head .atype{font-family:var(--mono); font-size:9px; font-weight:700; letter-spacing:.04em; padding:3px 6px; border-radius:5px; color:var(--on-accent);}
    .atype.md{background:var(--type-md);} .atype.html{background:var(--accent);} .atype.svg{background:var(--amber);} .atype.py{background:var(--sage);}
    .art-head .aname{font-family:var(--mono); font-size:12.5px; font-weight:500; color:var(--ink); white-space:nowrap; overflow:hidden; text-overflow:ellipsis; flex:1; min-width:0; unicode-bidi:isolate;}
    .art-head .gen{font-family:var(--mono); font-size:9.5px; color:var(--ink-faint); display:flex; align-items:center; gap:4px; flex:none;}
    .art-head .gen .gd{width:5px; height:5px; border-radius:50%; background:var(--accent);}
    .art-doc[data-mode="source"] .render{display:none;}
    .art-doc[data-mode="render"] .source{display:none;}
    .art-body{flex:1; min-height:0; overflow:auto; position:relative;}
    /* raw-text source view, shared by the markdown/html/svg viewers */
    .source-view{padding:18px 20px 40px; font-family:var(--mono); font-size:12px; line-height:1.7; color:var(--ink); white-space:pre-wrap; tab-size:2;}
  `,
  // active: () => boolean — whether this artifact's tab is selected.
  view: ({ artifact, active } = {}) => {
    // ── logic ───────────────────────────────────
    const mode = van.state("render");
    const Viewer = VIEWERS[artifact.kind] || CodeArtifact;
    const renderLabel = artifact.kind === "html" || artifact.kind === "svg"
      ? "Preview"
      : "Rendered";

    // ── content ─────────────────────────────────
    return section(
      {
        class: () => "art-doc" + (active() ? " active" : ""),
        "data-type": artifact.kind,
        "data-mode": () => mode.val,
      },
      artifact.kind === "py"
        ? ArtifactHead({ artifact, mode })
        : ArtifactHead({ artifact, mode, renderLabel }),
      Viewer({ artifact, mode }),
    );
  },
});
