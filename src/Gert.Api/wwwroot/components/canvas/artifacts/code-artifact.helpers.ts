// components/canvas/artifacts/code-artifact.helpers.js - pure severity helpers for
// the code viewer's gutter dots + Problems panel. No van, no DOM: the viewer maps
// these onto its reactive bindings (code-artifact.js). Problems are wire-shaped
// (model/tool-authored), so every field is read defensively.

// Normalized gutter/problem severity.
export type Sev = "err" | "warn";

// One linter diagnostic on a code artifact. Wire-shaped (model/tool-authored),
// so every field is optional and read defensively. The `Artifact` store row types
// `problems` as a string; here we work with the runtime array shape the code
// viewer actually receives, declared locally rather than re-typing the store row
// (the code viewer is the only place that reads the array).
export interface Problem {
  severity?: string;
  message?: string;
  code?: string;
  line?: number;
  col?: number;
}

const SEV: Record<string, Sev> = { error: "err", err: "err", warning: "warn", warn: "warn" };

export const sevClass = (s: string | undefined): Sev => (s !== undefined && SEV[s]) || "warn";

export const ICON: Record<Sev, string> = { err: "x", warn: "!" };

// Worst severity per 1-based line (err outranks warn) for the gutter dot.
export const worstSevByLine = (problems: Problem[]): Record<number, Sev> => {
  const lineSev: Record<number, Sev> = {};
  for (const p of problems) {
    if (!p.line) continue;
    const c = sevClass(p.severity);
    if (c === "err" || lineSev[p.line] !== "err") lineSev[p.line] = c;
  }
  return lineSev;
};
