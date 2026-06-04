// components/canvas/artifacts/svg-artifact.js — sandboxed-iframe render + raw
// source. SVG can carry <script>/foreignObject, so it renders at an opaque
// origin with a fully restrictive sandbox (no allow-scripts) — same F3 posture
// as the HTML viewer, minus script execution.
import van from "van";

const { div, iframe } = van.tags;

export const SvgArtifact = ({ artifact } = {}) =>
  div(
    { class: "art-body" },
    div(
      { class: "render" },
      div({ class: "svg-stage" }, () => {
        const f = iframe({ title: (artifact.name || "SVG") + " preview" });
        f.setAttribute("sandbox", ""); // fully sandboxed: no scripts, opaque origin
        f.srcdoc = artifact.content || "";
        return f;
      }),
    ),
    div(
      { class: "source" },
      div({ class: "source-view" }, () => artifact.content || ""),
    ),
  );
