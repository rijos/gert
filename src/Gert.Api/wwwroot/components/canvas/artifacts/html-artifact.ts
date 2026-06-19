// Preview (render) + raw source.
//
// F3, two layers of isolation:
//  1. PREFERRED - render from the SEPARATE artifact origin: mint a short-lived
//     signed ticket (authed, pid-scoped) and frame `.../artifacts/raw?t=...`. That
//     document gets its OWN, non-inherited CSP (script-src 'unsafe-inline' ->
//     fidelity; connect-src 'none' -> no egress) plus `sandbox allow-scripts` ->
//     opaque origin. Cross-origin from the app, so even a sandbox-escape can't
//     reach the token/DOM.
//  2. FALLBACK - if ticketing fails (offline, artifact not yet persisted, no
//     origin configured), drop to an in-place `srcdoc` with the same posture via
//     artifactSrcdoc()'s meta-CSP. Always sandbox="allow-scripts", never
//     allow-same-origin. (src and srcdoc are mutually exclusive - srcdoc wins -
//     so we set exactly one.)
import van from "/lib/van.js";
import { component } from "../../../lib/component.js";
import { MdCode } from "./md-code.js";
import { ArtifactSource } from "./artifact-source.js";
import { artifactSrcdoc } from "../../../lib/artifact-sandbox.js";
import * as artifactsSvc from "../../../services/artifacts.js";
import type { Artifact } from "../../../state/artifacts.js";

const { div, iframe } = van.tags;

export const HtmlArtifact = component({
  name: "html-artifact",
  css: `
    .html-stage {
      height: 100%;
      background: var(--preview-bg);
      display: flex;
    }
    .html-stage iframe {
      width: 100%;
      height: 100%;
      border: none;
      background: var(--preview-bg);
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
    div({ class: "render html-stage" }, () => {
      const f = iframe({
        sandbox: "allow-scripts",
        title: (artifact.name || "HTML") + " preview",
      });
      const fallback = () => {
        // srcdoc only takes effect if src is unset - clear any stale src first.
        f.removeAttribute("src");
        f.srcdoc = artifactSrcdoc(artifact.content, { allowScripts: true });
      };
      // Prefer the separate-origin served render; fall back to in-place srcdoc.
      // The ticket fetch lives in services/artifacts.js (section 6 - no http.* in
      // components); it derives the project id from the store.
      if (artifact.id) {
        artifactsSvc
          .ticket(artifact.id)
          // a missing/blank url falls through to the in-place srcdoc.
          .then((r) => {
            if (r.url) f.src = r.url;
            else fallback();
          })
          .catch(fallback);
      } else {
        fallback();
      }
      return f;
    }),
    // tinted markup source via the MdCode leaf - it tints from textContent into
    // inert tok-* spans, so the document is never interpreted here (the iframe
    // stays the only place it runs, sandboxed).
    ArtifactSource({ body: () => MdCode({ code: artifact.content || "", lang: "html" }) }),
  ),
});
