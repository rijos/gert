// worker.mjs - the isolated render-runner. Each input is rendered HERE, in a
// worker thread, so the parent can impose a hard per-input timeout: a genuine
// infinite loop or pathological super-linear input hangs only THIS thread, and
// the parent terminates + respawns it (-> a definitive "hang" verdict). The
// worker classifies each input against the oracle and reports timing.
//
// Protocol: parent posts a string `input`; worker replies
//   { status: 'ok'|'throw'|'violation', ms, detail }
// (a missing reply within the parent's timeout is treated as 'hang').

import { parentPort } from "node:worker_threads";
import { checkSecurity, checkBounds, checkWellFormed, serialize } from "./oracle.mjs";

// The renderer module is overridable so the harness can be SELF-TESTED against
// deliberately-buggy renderers (selftest.mjs) - proving the oracle/worker/
// minimizer actually catch throws, F4 violations, hangs, and nondeterminism.
const RENDER_MODULE = process.env.GERT_RENDER_MODULE || "./render.mjs";
const { renderMarkdown } = await import(RENDER_MODULE);

function classify(input) {
  const src = String(input);
  const t0 = process.hrtime.bigint();
  let frag;
  try {
    frag = renderMarkdown(src);
  } catch (e) {
    const ms = Number(process.hrtime.bigint() - t0) / 1e6;
    return { status: "throw", ms, detail: (e && e.stack) || String(e) };
  }
  const ms = Number(process.hrtime.bigint() - t0) / 1e6;

  // contract: must return a fragment (nodeType 11)
  if (!frag || frag.nodeType !== 11) return { status: "violation", ms, detail: "renderMarkdown did not return a DocumentFragment" };

  const sec = checkSecurity(frag);
  if (sec) return { status: "violation", ms, detail: "SECURITY: " + sec.slice(0, 5).join(" | ") };
  const wf = checkWellFormed(frag);
  if (wf) return { status: "violation", ms, detail: "WELLFORMED: " + wf };
  const bnd = checkBounds(frag, src.length);
  if (bnd) return { status: "violation", ms, detail: "BOUNDS: " + bnd };

  // determinism: a second render must be byte-identical (no leaked state)
  let frag2;
  try { frag2 = renderMarkdown(src); } catch (e) { return { status: "violation", ms, detail: "NONDETERMINISM: 2nd render threw: " + e }; }
  if (serialize(frag) !== serialize(frag2)) return { status: "violation", ms, detail: "NONDETERMINISM: outputs differ across renders" };

  return { status: "ok", ms, detail: "" };
}

parentPort.on("message", (input) => {
  let res;
  try { res = classify(input); }
  catch (e) { res = { status: "throw", ms: -1, detail: "worker fault: " + ((e && e.stack) || e) }; }
  parentPort.postMessage(res);
});
