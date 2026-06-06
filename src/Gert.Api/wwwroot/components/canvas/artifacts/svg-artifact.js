// components/canvas/artifacts/svg-artifact.js — sandboxed-iframe render + raw
// source. SVG can carry <script>/foreignObject, so it renders at an opaque
// origin with a fully restrictive sandbox (no allow-scripts) — same F3 posture
// as the HTML viewer, minus script execution.
import van from "van";
import { component } from "../../../lib/component.js";
import { highlight } from "../../../lib/highlight.js";

const { div, iframe } = van.tags;

export const SvgArtifact = component({
  name: "svg-artifact",
  css: `
    .svg-stage{height:100%; display:flex;
      background-color:var(--inset);
      background-image:linear-gradient(45deg,var(--line) 25%,transparent 25%),linear-gradient(-45deg,var(--line) 25%,transparent 25%),linear-gradient(45deg,transparent 75%,var(--line) 75%),linear-gradient(-45deg,transparent 75%,var(--line) 75%);
      background-size:16px 16px; background-position:0 0,0 8px,8px -8px,-8px 0;}
    .svg-stage iframe{width:100%; height:100%; border:none; background:transparent;}
  `,
  view: ({ artifact } = {}) =>
  div(
    { class: "art-body" },
    // the stage IS the .render wrapper: its height:100% must resolve against
    // .art-body (definite, flex) — an intermediate auto-height div would
    // collapse the iframe to its default size instead of filling the pane.
    div({ class: "render svg-stage" }, () => {
      const f = iframe({ title: (artifact.name || "SVG") + " preview" });
      f.setAttribute("sandbox", ""); // fully sandboxed: no scripts, opaque origin
      f.srcdoc = artifact.content || "";
      return f;
    }),
    div(
      { class: "source" },
      // tinted markup source — DOM nodes from textContent, never interpreted.
      div({ class: "source-view" }, () => {
        const host = div();
        host.append(...highlight(artifact.content || "", "svg"));
        return host;
      }),
    ),
  ),
});
