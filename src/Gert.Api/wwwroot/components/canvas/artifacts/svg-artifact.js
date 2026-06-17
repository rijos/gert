// components/canvas/artifacts/svg-artifact.js - sandboxed-iframe render + raw
// source. SVG can carry <script>/foreignObject, so it renders at an opaque
// origin with a fully restrictive sandbox (no allow-scripts) - same F3 posture
// as the HTML viewer, minus script execution.
import van from "/lib/van.js";
import { component } from "../../../lib/component.js";
import { MdCode } from "./md-code.js";
import { artifactSrcdoc } from "../../../lib/artifact-sandbox.js";

const { div, iframe } = van.tags;

export const SvgArtifact = component({
  name: "svg-artifact",
  css: `
    .svg-stage{height:100%; display:flex;
      background-color:var(--surface-2);
      background-image:linear-gradient(45deg,var(--line) 25%,transparent 25%),linear-gradient(-45deg,var(--line) 25%,transparent 25%),linear-gradient(45deg,transparent 75%,var(--line) 75%),linear-gradient(-45deg,transparent 75%,var(--line) 75%);
      background-size:16px 16px; background-position:0 0,0 8px,8px -8px,-8px 0;}
    .svg-stage iframe{width:100%; height:100%; border:none; background:transparent;}
    /* MdCode wraps the tinted source in <pre><code>; strip its UA chrome so the
       source view reads exactly as the bare-tokens div did (the .source-view in
       artifact.js owns the mono font, --code-bg ground, padding and pre-wrap). */
    .source-view pre{margin:0; padding:0; background:none; overflow:visible; white-space:inherit; font:inherit;}
    .source-view pre code{font:inherit; background:none; color:inherit;}
  `,
  view: ({ artifact } = {}) =>
  div(
    { class: "art-body" },
    // the stage IS the .render wrapper: its height:100% must resolve against
    // .art-body (definite, flex) - an intermediate auto-height div would
    // collapse the iframe to its default size instead of filling the pane.
    div({ class: "render svg-stage" }, () => {
      const f = iframe({ title: (artifact.name || "SVG") + " preview" });
      f.setAttribute("sandbox", ""); // fully sandboxed: no scripts, opaque origin
      // Even script-free, SVG can beacon via <image href>/<use href>/external
      // CSS - the per-document CSP (no scripts, no external fetch) closes that.
      f.srcdoc = artifactSrcdoc(artifact.content, { allowScripts: false });
      return f;
    }),
    div(
      { class: "source" },
      // tinted markup source via the MdCode leaf - tinted from textContent into
      // inert tok-* spans, never interpreted.
      div({ class: "source-view" }, () =>
        MdCode({ code: artifact.content || "", lang: "svg" }),
      ),
    ),
  ),
});
