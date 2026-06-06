// components/canvas/artifacts/html-artifact.js — preview (render) + raw source.
//
// F3, two layers of isolation:
//  1. PREFERRED — render from the SEPARATE artifact origin: mint a short-lived
//     signed ticket (authed, pid-scoped) and frame `…/artifacts/raw?t=…`. That
//     document gets its OWN, non-inherited CSP (script-src 'unsafe-inline' →
//     fidelity; connect-src 'none' → no egress) plus `sandbox allow-scripts` →
//     opaque origin. Cross-origin from the app, so even a sandbox-escape can't
//     reach the token/DOM.
//  2. FALLBACK — if ticketing fails (offline, artifact not yet persisted, no
//     origin configured), drop to an in-place `srcdoc` with the same posture via
//     artifactSrcdoc()'s meta-CSP. Always sandbox="allow-scripts", never
//     allow-same-origin. (src and srcdoc are mutually exclusive — srcdoc wins —
//     so we set exactly one.)
import van from "van";
import { component } from "../../../lib/component.js";
import { highlight } from "../../../lib/highlight.js";
import { artifactSrcdoc } from "../../../lib/artifact-sandbox.js";
import * as http from "../../../services/http.js";
import * as chat from "../../../state/chat.js";

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
      const fallback = () => {
        // srcdoc only takes effect if src is unset — clear any stale src first.
        f.removeAttribute("src");
        f.srcdoc = artifactSrcdoc(artifact.content, { allowScripts: true });
      };
      const pid = chat.activeProjectId.val;
      // Prefer the separate-origin served render; fall back to in-place srcdoc.
      if (pid && artifact.id) {
        http
          .get(`/projects/${pid}/artifacts/${artifact.id}/ticket`)
          .then((r) => {
            if (r?.url) f.src = r.url;
            else fallback();
          })
          .catch(fallback);
      } else {
        fallback();
      }
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
