// components/canvas/artifacts/html-artifact.js — sandboxed-iframe preview (render)
// + raw source. F3: the iframe gets `allow-scripts` for fidelity but NEVER
// `allow-same-origin`, so the document runs at an opaque origin and cannot reach
// the app's cookies, storage, or DOM.
import van from "van";

const { div, iframe } = van.tags;

export const HtmlArtifact = ({ artifact } = {}) =>
  div(
    { class: "art-body" },
    div(
      { class: "render" },
      div({ class: "html-stage" }, () => {
        const f = iframe({
          sandbox: "allow-scripts",
          title: (artifact.name || "HTML") + " preview",
        });
        f.srcdoc = artifact.content || "";
        return f;
      }),
    ),
    div(
      { class: "source" },
      div({ class: "source-view" }, () => artifact.content || ""),
    ),
  );
