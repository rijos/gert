// components/canvas/artifacts/code-artifact.js — linted source view: numbered
// lines with warn/err gutter dots + a Problems panel. The default viewer for
// `py` and any unknown kind. No Rendered/Source toggle — the head shows a
// problem count instead (see artifact-head.js). problems: [{ severity, message,
// code, line, col }].
import van from "van";

const { div, span } = van.tags;

const SEV = { error: "err", err: "err", warning: "warn", warn: "warn" };
const sevClass = (s) => SEV[s] || "warn";
const ICON = { err: "✕", warn: "▲" }; // ✕  ▲

export const CodeArtifact = ({ artifact } = {}) =>
  div(
    { class: "art-body" },
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
        const lines = String(artifact.content ?? "").split("\n");
        return div(
          { class: "code-wrap" },
          ...lines.map((text, i) => {
            const sev = lineSev[i + 1];
            return div(
              { class: "cline" + (sev ? " " + sev : "") },
              span({ class: "lnum" }, String(i + 1)),
              span({ class: "lcode" }, text),
            );
          }),
        );
      }),
      // problems panel — collapsed to nothing when the artifact is clean.
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
  );
