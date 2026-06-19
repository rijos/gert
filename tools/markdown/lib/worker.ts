// worker.ts - the isolated render-runner. Each input is rendered HERE, in a
// worker thread, so the parent can impose a hard per-input timeout: a genuine
// infinite loop or pathological super-linear input hangs only THIS thread, and
// the parent terminates + respawns it (-> a definitive "hang" verdict). The
// worker classifies each input against the oracle and reports timing.
//
// Protocol: parent posts a string `input`; worker replies
//   { status: 'ok'|'throw'|'violation', ms, detail }
// (a missing reply within the parent's timeout is treated as 'hang').

import { parentPort } from "node:worker_threads";
import { checkSecurity, checkBounds, checkWellFormed, serialize } from "./oracle.ts";

// The worker -> parent reply protocol (see header). detail carries the violation
// or throw text; ms is the render time (or -1 for a worker fault).
interface WorkerResult {
  status: "ok" | "throw" | "violation";
  ms: number;
  detail: string;
}

// The renderer module is overridable so the harness can be SELF-TESTED against
// deliberately-buggy renderers (selftest.ts) - proving the oracle/worker/
// minimizer actually catch throws, F4 violations, hangs, and nondeterminism.
const RENDER_MODULE = process.env.GERT_RENDER_MODULE || "./render.ts";
// The render module is resolved by a runtime env var, so it is outside this tsgo
// program; narrow its single consumed export at this dynamic boundary.
const renderModule = await import(RENDER_MODULE) as { renderMarkdown: (src: string) => unknown };
const { renderMarkdown } = renderModule;

// Extract a stack/string from an unknown thrown value (catch is `unknown`).
function errText(e: unknown): string {
  if (e instanceof Error && e.stack) return e.stack;
  return String(e);
}

function classify(input: unknown): WorkerResult {
  const src = String(input);
  const t0 = process.hrtime.bigint();
  let frag: unknown;
  try {
    frag = renderMarkdown(src);
  } catch (e) {
    const ms = Number(process.hrtime.bigint() - t0) / 1e6;
    return { status: "throw", ms, detail: errText(e) };
  }
  const ms = Number(process.hrtime.bigint() - t0) / 1e6;

  // contract: must return a fragment (nodeType 11). Narrow the unknown render
  // output at this boundary: read nodeType off it to decide.
  const fragNode = frag as { nodeType?: number } | null | undefined;
  if (!fragNode || fragNode.nodeType !== 11) return { status: "violation", ms, detail: "renderMarkdown did not return a DocumentFragment" };

  const sec = checkSecurity(frag);
  if (sec) return { status: "violation", ms, detail: "SECURITY: " + sec.slice(0, 5).join(" | ") };
  const wf = checkWellFormed(frag);
  if (wf) return { status: "violation", ms, detail: "WELLFORMED: " + wf };
  const bnd = checkBounds(frag, src.length);
  if (bnd) return { status: "violation", ms, detail: "BOUNDS: " + bnd };

  // determinism: a second render must be byte-identical (no leaked state)
  let frag2: unknown;
  try { frag2 = renderMarkdown(src); } catch (e) { return { status: "violation", ms, detail: "NONDETERMINISM: 2nd render threw: " + e }; }
  if (serialize(frag) !== serialize(frag2)) return { status: "violation", ms, detail: "NONDETERMINISM: outputs differ across renders" };

  return { status: "ok", ms, detail: "" };
}

// invariant: this module only runs inside a worker thread, where parentPort is set.
parentPort!.on("message", (input: unknown) => {
  let res: WorkerResult;
  try { res = classify(input); }
  catch (e) { res = { status: "throw", ms: -1, detail: "worker fault: " + errText(e) }; }
  parentPort!.postMessage(res);
});
