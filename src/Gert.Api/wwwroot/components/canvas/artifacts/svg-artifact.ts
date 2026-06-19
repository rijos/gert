// components/canvas/artifacts/svg-artifact.js - sandboxed-iframe render + raw
// source. SVG can carry <script>/foreignObject, so it renders at an opaque
// origin with a fully restrictive sandbox (no allow-scripts) - same F3 posture
// as the HTML viewer, minus script execution.
import van from "/lib/van.js";
import { component } from "../../../lib/component.js";
import { MdCode } from "./md-code.js";
import { ArtifactSource } from "./artifact-source.js";
import { artifactSrcdoc } from "../../../lib/artifact-sandbox.js";
import type { Artifact } from "../../../state/artifacts.js";

const { div, iframe } = van.tags;

export const SvgArtifact = component({
  name: "svg-artifact",
  css: `
    .svg-stage {
      height: 100%;
      display: flex;
      background-color: var(--surface-2);
      background-image: linear-gradient(45deg,var(--line) 25%,transparent 25%),linear-gradient(-45deg,var(--line) 25%,transparent 25%),linear-gradient(45deg,transparent 75%,var(--line) 75%),linear-gradient(-45deg,transparent 75%,var(--line) 75%);
      background-size: 16px 16px;
      background-position: 0 0,0 8px,8px -8px,-8px 0;
    }
    .svg-stage iframe {
      width: 100%;
      height: 100%;
      border: none;
      background: transparent;
    }
  `,
  // `artifact` is the store row; the `= {}` default is preserved verbatim from
  // the JS (dead defensive code - artifact.js always dispatches a real row), so
  // the empty default is cast to the prop shape rather than widening every access.
  view: ({ artifact }: { artifact: Artifact } = {} as { artifact: Artifact }) =>
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
    // raw source via MdCode: tinted from textContent into inert tok-* spans, never interpreted.
    ArtifactSource({ body: () => MdCode({ code: artifact.content || "", lang: "svg" }) }),
  ),
});
