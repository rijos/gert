// components/canvas/artifacts/html-artifact.js — sandboxed-iframe preview (render)
// + raw source. F3: the iframe gets `allow-scripts` for fidelity but NEVER
// `allow-same-origin`, so the document runs at an opaque origin and cannot reach
// the app's cookies, storage, or DOM. artifactSrcdoc() adds the second half of
// F3 — a per-document CSP so the page can't beacon data out or post a phishing
// form (connect-src/form-action/img locked to nothing external).
import van from "van";
import { component } from "../../../lib/component.js";
import { highlight } from "../../../lib/highlight.js";
import { artifactSrcdoc } from "../../../lib/artifact-sandbox.js";

const { div, iframe } = van.tags;

export const HtmlArtifact = component({
  name: "html-artifact",
  css: `
    .html-stage{height:100%; background:var(--preview-bg); display:flex;}
    .html-stage iframe{width:100%; height:100%; border:none; background:var(--preview-bg);}
  `,
  view: ({ artifact } = {}) =>
  div(
    { class: "art-body" },
    // the stage IS the .render wrapper: its height:100% must resolve against
    // .art-body (definite, flex) — an intermediate auto-height div would
    // collapse the iframe to its default size instead of filling the pane.
    div({ class: "render html-stage" }, () => {
      const f = iframe({
        sandbox: "allow-scripts",
        title: (artifact.name || "HTML") + " preview",
      });
      f.srcdoc = artifactSrcdoc(artifact.content, { allowScripts: true });
      return f;
    }),
    div(
      { class: "source" },
      // tinted markup source — highlight() emits DOM nodes from textContent,
      // so the document is never interpreted here (the iframe stays the only
      // place it runs, sandboxed).
      div({ class: "source-view" }, () => {
        const host = div();
        host.append(...highlight(artifact.content || "", "html"));
        return host;
      }),
    ),
  ),
});
