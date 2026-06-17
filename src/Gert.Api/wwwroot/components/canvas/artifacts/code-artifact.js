// components/canvas/artifacts/code-artifact.js - linted code viewer for the
// code kinds (py/cs/cpp/js/rs) and any unknown kind. Preview mode: numbered,
// syntax-highlighted lines with warn/err gutter dots + a Problems panel.
// Source mode: the raw tinted text without line numbers (clean to copy) -
// both wrappers are always built; .art-doc[data-mode] picks one (artifact.js).
// problems: [{ severity, message, code, line, col }].
import van from "/lib/van.js";
import { component } from "../../../lib/component.js";
import { MdCode } from "./md-code.js";
import { highlightLines } from "../../../lib/highlight.js";

const { div, span } = van.tags;

const SEV = { error: "err", err: "err", warning: "warn", warn: "warn" };
const sevClass = (s) => SEV[s] || "warn";
const ICON = { err: "x", warn: "!" };

export const CodeArtifact = component({
  name: "code-artifact",
  css: `
    .code-render{height:100%;}
    .code-stage{display:flex; flex-direction:column; height:100%;}
    /* code surfaces sit on the dark --code-bg ground in both themes (tokens.css) */
    .code-scroll{flex:1; overflow:auto; background:var(--code-bg); color:var(--code-fg); padding:12px 0;}
    .code-wrap{font-family:var(--mono); font-size:var(--fs-sm); line-height:1.75; min-width:max-content;}
    .cline{display:flex; position:relative;}
    .cline:hover{background:color-mix(in srgb, var(--code-fg) 7%, transparent);}
    .cline .lnum{width:42px; flex:none; text-align:right; padding-right:14px; color:var(--tok-com); user-select:none; position:relative;}
    .cline.warn .lnum::before,.cline.err .lnum::before{content:""; position:absolute; left:7px; top:50%; transform:translateY(-50%); width:6px; height:6px; border-radius:50%;}
    .cline.warn .lnum::before{background:var(--amber);}
    .cline.err .lnum::before{background:var(--brick);}
    .cline .lcode{white-space:pre; padding-right:20px;}
    .problems{flex:none; border-top:1px solid var(--line); background:var(--surface-2); max-height:38%; overflow:auto;}
    .prob-h{font-family:var(--mono); font-size:var(--fs-2xs); letter-spacing:.06em; text-transform:uppercase; color:var(--ink-3); padding:9px 14px 6px; display:flex; align-items:center; gap:7px;}
    .prob-h .cnt{background:var(--surface-2); border:1px solid var(--line); border-radius:10px; padding:1px 7px; color:var(--ink-2);}
    .prob{display:flex; align-items:baseline; gap:9px; padding:7px 14px; border-top:1px solid var(--line); cursor:pointer; transition:var(--t-fast);}
    .prob:hover{background:var(--surface-2);}
    .prob .pi{width:13px; flex:none; font-weight:700; text-align:center; align-self:center;}
    .prob.warn .pi{color:var(--amber);} .prob.err .pi{color:var(--brick);}
    .prob .pmsg{font-size:var(--fs-sm); color:var(--ink); flex:1;}
    .prob .pcode{font-family:var(--mono); font-size:var(--fs-2xs); color:var(--ink-3);}
    .prob .ploc{font-family:var(--mono); font-size:var(--fs-2xs); color:var(--ink-3); flex:none;}
    /* MdCode wraps the tinted source in <pre><code>; strip its UA chrome so the
       source view reads exactly as the bare-tokens div did (the .source-view in
       artifact.js owns the mono font, --code-bg ground, padding and pre-wrap). */
    .source-view pre{margin:0; padding:0; background:none; overflow:visible; white-space:inherit; font:inherit;}
    .source-view pre code{font:inherit; background:none; color:inherit;}
  `,
  // problems: [{ severity, message, code, line, col }]
  view: ({ artifact } = {}) =>
  div(
    { class: "art-body" },
    div(
      { class: "render code-render" },
      div(
      { class: "code-stage" },
      // numbered lines; re-renders as content/problems stream in.
      div({ class: "code-scroll" }, () => {
        const problems = artifact.problems || [];
        // worst severity per 1-based line (err outranks warn) for the gutter dot.
        const lineSev = {};
        for (const p of problems) {
          if (!p.line) continue;
          const c = sevClass(p.severity);
          if (c === "err" || lineSev[p.line] !== "err") lineSev[p.line] = c;
        }
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
      // problems panel - collapsed to nothing when the artifact is clean.
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
    div(
      { class: "source" },
      // raw tinted source without the line-number gutter, via the MdCode leaf -
      // it tints from textContent into inert tok-* spans, so this stays XSS-safe.
      // kind doubles as the language hint, exactly as highlight() consumed it.
      div({ class: "source-view" }, () =>
        MdCode({ code: String(artifact.content ?? ""), lang: artifact.kind }),
      ),
    ),
  ),
});
