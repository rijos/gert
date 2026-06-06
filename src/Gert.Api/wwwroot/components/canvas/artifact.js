// components/canvas/artifact.js — polymorphic dispatcher: picks a viewer by
// artifact.kind, wraps it with the shared header + Rendered/Source mode state.
// Owns the shared artifact shell CSS (.art-head, .art-body, .source-view, the
// data-mode render/source toggle). The header is a trivial single-use leaf, so
// it lives here as ArtifactHead rather than its own file.
import van from "van";
import { component } from "../../lib/component.js";
import { SegToggle } from "../ui/seg-toggle.js";
import { Icon } from "../../icons/icons.js";
import { MarkdownArtifact } from "./artifacts/markdown-artifact.js";
import { HtmlArtifact } from "./artifacts/html-artifact.js";
import { SvgArtifact } from "./artifacts/svg-artifact.js";
import { CodeArtifact } from "./artifacts/code-artifact.js";

const { section, div, span, button } = van.tags;

const VIEWERS = {
  md: MarkdownArtifact,
  html: HtmlArtifact,
  svg: SvgArtifact,
  py: CodeArtifact,
  cs: CodeArtifact,
  cpp: CodeArtifact,
  js: CodeArtifact,
  rs: CodeArtifact,
};

const TYPE_LABEL = {
  md: "MD",
  html: "HTML",
  svg: "SVG",
  py: "PY",
  cs: "CS",
  cpp: "CPP",
  js: "JS",
  rs: "RS",
};

// download MIME by kind; everything code-shaped saves as plain text.
const MIME = { md: "text/markdown", html: "text/html", svg: "image/svg+xml" };

// Save the artifact body under its tab name via a transient blob URL.
const downloadArtifact = (artifact) => {
  const type = (MIME[artifact.kind] || "text/plain") + ";charset=utf-8";
  const url = URL.createObjectURL(new Blob([artifact.content || ""], { type }));
  const a = document.createElement("a");
  a.href = url;
  a.download = artifact.name || "artifact.txt";
  a.click();
  URL.revokeObjectURL(url);
};

// type badge + name + Rendered/Source seg toggle (shared across viewers) +
// download. Code kinds have no rendered mode, so they show a problem count
// where the toggle would be. renderLabel lets html/svg say "Preview" instead
// of "Rendered". `mode` is the van.state owned by Artifact below.
const ArtifactHead = ({ artifact, mode, renderLabel = "Rendered", code = false } = {}) =>
  div(
    { class: "art-head" },
    span({ class: "atype " + artifact.kind }, TYPE_LABEL[artifact.kind] || "?"),
    // filename is a van text node — XSS-safe; CSS adds unicode-bidi:isolate.
    span({ class: "aname" }, artifact.name || "untitled"),
    code
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
    button(
      {
        class: "art-dl",
        title: "Download",
        "aria-label": "Download " + (artifact.name || "artifact"),
        onclick: () => downloadArtifact(artifact),
      },
      Icon("download", { size: 14, strokeWidth: 2 }),
    ),
  );

export const Artifact = component({
  name: "artifact",
  css: `
    .art-head{display:flex; align-items:center; gap:9px; padding:11px 13px; border-bottom:1px solid var(--line); flex:none;}
    .art-head .atype{font-family:var(--mono); font-size:9px; font-weight:700; letter-spacing:.04em; padding:3px 6px; border-radius:5px; color:var(--on-accent);}
    .atype.md{background:var(--type-md);} .atype.html{background:var(--accent);} .atype.svg{background:var(--amber);} .atype.py{background:var(--sage);}
    .atype.cs{background:var(--type-cs);} .atype.cpp{background:var(--type-cpp);} .atype.js{background:var(--type-js);} .atype.rs{background:var(--type-rs);}
    .art-head .aname{font-family:var(--mono); font-size:12.5px; font-weight:500; color:var(--ink); white-space:nowrap; overflow:hidden; text-overflow:ellipsis; flex:1; min-width:0; unicode-bidi:isolate;}
    .art-head .gen{font-family:var(--mono); font-size:9.5px; color:var(--ink-faint); display:flex; align-items:center; gap:4px; flex:none;}
    .art-head .gen .gd{width:5px; height:5px; border-radius:50%; background:var(--accent);}
    .art-head .art-dl{display:flex; align-items:center; justify-content:center; width:26px; height:26px; flex:none; background:none; border:1px solid var(--line); border-radius:7px; color:var(--ink-soft); cursor:pointer; transition:.14s;}
    .art-head .art-dl:hover{border-color:var(--accent); color:var(--accent);}
    .art-doc[data-mode="source"] .render{display:none;}
    .art-doc[data-mode="render"] .source{display:none;}
    .art-body{flex:1; min-height:0; overflow:auto; position:relative;}
    /* highlighted source view, shared by the markdown/html/svg viewers */
    .source-view{padding:18px 20px 40px; font-family:var(--mono); font-size:12px; line-height:1.7; color:var(--ink); white-space:pre-wrap; tab-size:2;}
  `,
  // active: () => boolean — whether this artifact's tab is selected.
  view: ({ artifact, active } = {}) => {
    // ── logic ───────────────────────────────────
    const mode = van.state("render");
    const Viewer = VIEWERS[artifact.kind] || CodeArtifact;
    const code = Viewer === CodeArtifact; // no rendered mode for code kinds
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
      ArtifactHead({ artifact, mode, renderLabel, code }),
      Viewer({ artifact, mode }),
    );
  },
});
