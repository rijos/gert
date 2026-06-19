// components/canvas/artifacts/artifact-source.js - the raw "Source" pane shared by
// every artifact viewer (code/html/markdown/svg). Wraps the caller's tinted-source
// binding in the .source / .source-view shell; the parent .art-doc[data-mode]
// toggles it against the .render pane (artifact.js owns that toggle + the
// .source-view base rule). `body` is the viewer's own `() => MdCode(...)` binding,
// passed through verbatim so its reactive read of artifact.content is unchanged.
import van from "/lib/van.js";
import { component } from "../../../lib/component.js";

const { div } = van.tags;

export const ArtifactSource = component({
  name: "artifact-source",
  css: `
    /* MdCode wraps the tinted source in <pre><code>; strip its UA chrome so the
       source view reads exactly as the bare-tokens div did (the .source-view in
       artifact.js owns the mono font, --code-bg ground, padding and pre-wrap). */
    .source-view pre {
      margin: 0;
      padding: 0;
      background: none;
      overflow: visible;
      white-space: inherit;
      font: inherit;
    }
    .source-view pre code {
      font: inherit;
      background: none;
      color: inherit;
    }
  `,
  // `body` is the viewer's reactive child binding (typically `() => MdCode(...)`).
  view: ({ body }: { body: () => Node }) =>
    div({ class: "source" }, div({ class: "source-view" }, body)),
});
