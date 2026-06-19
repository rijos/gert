// Polymorphic dispatcher: picks a viewer by artifact.kind, wraps it with the
// shared header + Preview/Source mode state. Owns the shared artifact shell CSS
// (.art-head, .art-body, .source-view, the data-mode render/source toggle). The
// header is a trivial single-use leaf, so it lives here as ArtifactHead rather
// than its own file.
import van from "/lib/van.js";
import type { State } from "/lib/van.js";
import { component } from "../../lib/component.js";
import type { Artifact as ArtifactRow } from "../../state/artifacts.js";
import { SegToggle } from "../ui/seg-toggle.js";
import { Icon } from "../../icons/icons.js";
import { t } from "../../lib/i18n.js";
import { MarkdownArtifact } from "./artifacts/markdown-artifact.js";
import { HtmlArtifact } from "./artifacts/html-artifact.js";
import { SvgArtifact } from "./artifacts/svg-artifact.js";
import { CodeArtifact } from "./artifacts/code-artifact.js";

const { section, div, span, button } = van.tags;

// The viewers' param types vary (e.g. code-artifact narrows `problems` to its
// lint shape), so ViewerFn is the uniform call signature the dispatcher uses;
// the `as unknown as` double casts below assert each viewer shares that contract.
type ViewerFn = (props: { artifact: ArtifactRow; mode: State<string> }) => Element;

const CodeViewer = CodeArtifact as unknown as ViewerFn;
const VIEWERS: Record<string, ViewerFn> = {
  md: MarkdownArtifact as unknown as ViewerFn,
  html: HtmlArtifact as unknown as ViewerFn,
  svg: SvgArtifact as unknown as ViewerFn,
  py: CodeViewer,
  cs: CodeViewer,
  cpp: CodeViewer,
  js: CodeViewer,
  rs: CodeViewer,
};

const TYPE_LABEL: Record<string, string> = {
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
const MIME: Record<string, string> = { md: "text/markdown", html: "text/html", svg: "image/svg+xml" };

// Save the artifact body under its tab name via a transient blob URL.
const downloadArtifact = (artifact: ArtifactRow) => {
  const type = (MIME[artifact.kind] || "text/plain") + ";charset=utf-8";
  const url = URL.createObjectURL(new Blob([artifact.content || ""], { type }));
  const a = document.createElement("a");
  a.href = url;
  a.download = artifact.name || "artifact.txt";
  a.click();
  URL.revokeObjectURL(url);
};

// type badge + name + Preview/Source seg toggle + download; code kinds also show
// a problem count. Every kind has both modes, so every head renders the same
// chrome at the same height. `mode` is the van.state owned by Artifact below.
const ArtifactHead = (
  { artifact, mode, code = false }: { artifact: ArtifactRow; mode: State<string>; code?: boolean },
) =>
  div(
    { class: "art-head" },
    span({ class: "atype " + artifact.kind }, TYPE_LABEL[artifact.kind] || "?"),
    // filename is a van text node - XSS-safe; CSS adds unicode-bidi:isolate.
    span({ class: "aname" }, artifact.name || "untitled"),
    code
      ? span(
          { class: "gen" },
          span({ class: "gd" }),
          () => (artifact.problems?.length || 0) + " problems",
        )
      : null,
    SegToggle({
      label: t("View mode"),
      options: [
        { value: "render", label: t("Preview") },
        { value: "source", label: t("Source") },
      ],
      value: () => mode.val,
      onSelect: (v: string) => (mode.val = v),
    }),
    button({ class: "art-dl", title: t("Download"), "aria-label": "Download " + (artifact.name || "artifact"), onclick: () => downloadArtifact(artifact) },
      Icon("download", { size: 14, strokeWidth: 2 }),
    ),
  );

export const Artifact = component({
  name: "artifact",
  css: `
    .art-head {
      display: flex;
      align-items: center;
      gap: 9px;
      padding: 11px 13px;
      border-bottom: 1px solid var(--line);
      flex: none;
    }

    .art-head .atype {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      font-weight: 700;
      letter-spacing: .04em;
      padding: 2px 6px;
      border-radius: 5px;
      color: var(--on-chip);
    }

    .atype.md {
      background: var(--type-md);
    }
    .atype.html {
      background: var(--coral);
    }
    .atype.svg {
      background: var(--amber);
    }
    .atype.py {
      background: var(--type-py);
    }
    .atype.cs {
      background: var(--type-cs);
    }
    .atype.cpp {
      background: var(--type-cpp);
    }
    .atype.js {
      background: var(--type-js);
    }
    .atype.rs {
      background: var(--type-rs);
    }

    .art-head .aname {
      font-family: var(--mono);
      font-size: var(--fs-sm);
      font-weight: 500;
      color: var(--ink);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      flex: 1;
      min-width: 0;
      unicode-bidi: isolate;
    }

    .art-head .gen {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      color: var(--ink-3);
      display: flex;
      align-items: center;
      gap: 4px;
      flex: none;
    }

    .art-head .gen .gd {
      width: 5px;
      height: 5px;
      border-radius: 50%;
      background: var(--coral);
    }

    .art-head .art-dl {
      display: flex;
      align-items: center;
      justify-content: center;
      width: 26px;
      height: 26px;
      flex: none;
      background: none;
      border: 1px solid var(--line);
      border-radius: 7px;
      color: var(--ink-2);
      cursor: pointer;
      transition: var(--t-fast);
    }

    .art-head .art-dl:hover {
      border-color: var(--coral);
      color: var(--coral);
    }

    .art-doc[data-mode="source"] .render {
      display: none;
    }

    .art-doc[data-mode="render"] .source {
      display: none;
    }

    .art-body {
      flex: 1;
      min-height: 0;
      overflow: auto;
      position: relative;
    }

    /* highlighted source view, shared by the markdown/html/svg viewers - code
       surfaces follow the theme via --code-bg/--code-fg (tokens.css) */
    .source-view {
      min-height: 100%;
      padding: 18px 20px 40px;
      font-family: var(--mono);
      font-size: var(--fs-sm);
      line-height: 1.7;
      background: var(--code-bg);
      color: var(--code-fg);
      white-space: pre-wrap;
      tab-size: 2;
    }
  `,
  // The DOM-scoped derive that flips the lazy-mount latch stays in `view`, not
  // here, because it must be pruned with the element.
  setup: ({ artifact }: { artifact: ArtifactRow; active: () => boolean }) => {
    const mode = van.state("render");
    const Viewer = VIEWERS[artifact.kind] || CodeViewer;
    const code = Viewer === CodeViewer;

    // Lazy mount: the viewer body fetches <img> sources and loads the artifact
    // iframe, and `display:none` does NOT stop the browser doing either - so
    // mounting every artifact's body up front fetches all of them (and their
    // external images) on page load, before the user opens anything. `seen`
    // latches true the first time this tab is activated and stays true, so the
    // body is built on first open and kept mounted across later tab switches.
    const seen = van.state(false);

    return { mode, Viewer, code, seen };
  },
  view: ({ mode, Viewer, code, seen }, { artifact, active }: { artifact: ArtifactRow; active: () => boolean }) => {
    const el = section({ class: () => "art-doc" + (active() ? " active" : ""), "data-type": artifact.kind, "data-mode": () => mode.val, role: "tabpanel", "aria-label": () => artifact.name || artifact.kind },
      ArtifactHead({ artifact, mode, code }),
      () => (seen.val ? Viewer({ artifact, mode }) : div({ class: "art-body" })),
    );
    // flip the latch on first activation; scoped to `el` so it's cleaned up with
    // the artifact. Reads only active(); setting seen=true again is a no-op.
    // van.d.ts copies vanjs-core's derive as 1-arg, but the runtime takes the
    // (init, dom) scope args this codebase relies on; cast names that real shape.
    (van.derive as (f: () => unknown, s: undefined, dom: Node) => unknown)(
      () => { if (active()) seen.val = true; }, undefined, el);
    return el;
  },
});
