// Linted code viewer for the code kinds (py/cs/cpp/js/rs) and any unknown kind.
// Preview mode: numbered, syntax-highlighted lines with warn/err gutter dots + a
// Problems panel. Source mode: raw tinted text without line numbers (clean to copy).
// Both wrappers are always built; .art-doc[data-mode] picks one (artifact.js).
import van from "/lib/van.js";
import { component } from "../../../lib/component.js";
import { MdCode } from "./md-code.js";
import { ArtifactSource } from "./artifact-source.js";
import { sevClass, ICON, worstSevByLine } from "./code-artifact.helpers.js";
import type { Problem } from "./code-artifact.helpers.js";
import { highlightLines } from "../../../lib/highlight.js";
import type { Artifact } from "../../../state/artifacts.js";

const { div, span } = van.tags;

// The store row, but with `problems` widened to the diagnostic array the viewer
// consumes - via this intersection only, no change to the store interface.
type CodeArtifact = Omit<Artifact, "problems"> & { problems?: Problem[] };

export const CodeArtifact = component({
  name: "code-artifact",
  css: `
    .code-render {
      height: 100%;
    }
    .code-stage {
      display: flex;
      flex-direction: column;
      height: 100%;
    }
    /* code surfaces sit on the dark --code-bg ground in both themes (tokens.css) */
    .code-scroll {
      flex: 1;
      overflow: auto;
      background: var(--code-bg);
      color: var(--code-fg);
      padding: 12px 0;
    }
    .code-wrap {
      font-family: var(--mono);
      font-size: var(--fs-sm);
      line-height: 1.75;
      min-width: max-content;
    }
    .cline {
      display: flex;
      position: relative;
    }
    .cline:hover {
      background: color-mix(in srgb, var(--code-fg) 7%, transparent);
    }
    .cline .lnum {
      width: 42px;
      flex: none;
      text-align: right;
      padding-right: 14px;
      color: var(--tok-com);
      user-select: none;
      position: relative;
    }
    .cline.warn .lnum::before,.cline.err .lnum::before {
      content: "";
      position: absolute;
      left: 7px;
      top: 50%;
      transform: translateY(-50%);
      width: 6px;
      height: 6px;
      border-radius: 50%;
    }
    .cline.warn .lnum::before {
      background: var(--amber);
    }
    .cline.err .lnum::before {
      background: var(--brick);
    }
    .cline .lcode {
      white-space: pre;
      padding-right: 20px;
    }
    .problems {
      flex: none;
      border-top: 1px solid var(--line);
      background: var(--surface-2);
      max-height: 38%;
      overflow: auto;
    }
    .prob-h {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      letter-spacing: .06em;
      text-transform: uppercase;
      color: var(--ink-3);
      padding: 9px 14px 6px;
      display: flex;
      align-items: center;
      gap: 7px;
    }
    .prob-h .cnt {
      background: var(--surface-2);
      border: 1px solid var(--line);
      border-radius: 10px;
      padding: 1px 7px;
      color: var(--ink-2);
    }
    .prob {
      display: flex;
      align-items: baseline;
      gap: 9px;
      padding: 7px 14px;
      border-top: 1px solid var(--line);
      cursor: pointer;
      transition: var(--t-fast);
    }
    .prob:hover {
      background: var(--surface-2);
    }
    .prob .pi {
      width: 13px;
      flex: none;
      font-weight: 700;
      text-align: center;
      align-self: center;
    }
    .prob.warn .pi {
      color: var(--amber);
    }
    .prob.err .pi {
      color: var(--brick);
    }
    .prob .pmsg {
      font-size: var(--fs-sm);
      color: var(--ink);
      flex: 1;
    }
    .prob .pcode {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      color: var(--ink-3);
    }
    .prob .ploc {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      color: var(--ink-3);
      flex: none;
    }
  `,
  // The `= {}` default is dead defensive code preserved verbatim from the JS
  // (artifact.js always dispatches a real row), cast to the prop shape.
  view: ({ artifact }: { artifact: CodeArtifact } = {} as { artifact: CodeArtifact }) =>
  div(
    { class: "art-body" },
    div(
      { class: "render code-render" },
      div(
      { class: "code-stage" },
      // function child: re-renders as content/problems stream in.
      div({ class: "code-scroll" }, () => {
        const problems = artifact.problems || [];
        // worst severity per 1-based line (err outranks warn) for the gutter dot.
        const lineSev = worstSevByLine(problems);
        // per-line token nodes - kind doubles as the language hint (the
        // highlighter aliases py->python, cs->csharp, ...; unknown stays plain).
        const lines = highlightLines(String(artifact.content ?? ""), artifact.kind);
        return div(
          { class: "code-wrap" },
          ...lines.map((nodes, i) => {
            const sev = lineSev[i + 1];
            return div(
              { class: "cline" + (sev ? " " + sev : "") },
              span({ class: "lnum" }, String(i + 1)),
              span({ class: "lcode" }, ...nodes),
            );
          }),
        );
      }),
      // problems panel.
      () => {
        const problems = artifact.problems || [];
        if (!problems.length) return div({ style: "display:none" });
        return div(
          { class: "problems" },
          div(
            { class: "prob-h" },
            "Problems ",
            span({ class: "cnt" }, String(problems.length)),
          ),
          ...problems.map((p) => {
            const c = sevClass(p.severity);
            return div(
              { class: "prob " + c },
              span({ class: "pi" }, ICON[c]),
              span({ class: "pmsg" }, p.message || ""),
              span({ class: "pcode" }, p.code || ""),
              span(
                { class: "ploc" },
                p.line != null
                  ? p.line + (p.col != null ? ":" + p.col : "")
                  : "",
              ),
            );
          }),
        );
      },
      ),
    ),
    // raw tinted source without the line-number gutter, via the MdCode leaf -
    // it tints from textContent into inert tok-* spans, so this stays XSS-safe.
    // kind doubles as the language hint, exactly as highlight() consumed it.
    ArtifactSource({
      body: () => MdCode({ code: String(artifact.content ?? ""), lang: artifact.kind }),
    }),
  ),
});
