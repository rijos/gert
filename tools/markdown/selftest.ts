// selftest.ts - proves the FUZZER ITSELF works. A fuzzer that never reports a
// failure is indistinguishable from a broken one, so here we run the real worker
// against deliberately-broken renderers and assert the oracle/worker/minimizer
// catch each failure class. Also confirms the real renderer passes a known-good
// battery (no false positives). Run: node selftest.ts
//
// Each buggy renderer is a tiny module written to .selftest/ and injected via the
// GERT_RENDER_MODULE env the worker honors.

import { Worker } from "node:worker_threads";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { mkdirSync, writeFileSync, rmSync } from "node:fs";

// The worker's reply protocol (lib/worker.ts): a status verdict, timing, and a detail string.
// Messages arrive as `unknown` over the thread channel; narrow with a localized cast at receipt.
interface Verdict { status: string; ms: number; detail: string; }

const HERE = dirname(fileURLToPath(import.meta.url));
const WORKER = join(HERE, "lib", "worker.ts");
const TMP = join(HERE, ".selftest");
mkdirSync(TMP, { recursive: true });

// run one input through the worker, with a watchdog, using a given render module.
function runWith(moduleAbsPath: string, input: string, hangMs = 1500): Promise<Verdict> {
  return new Promise((resolve) => {
    const w = new Worker(WORKER, { env: { ...process.env, GERT_RENDER_MODULE: moduleAbsPath } });
    let done = false;
    const finish = (r: Verdict) => { if (done) return; done = true; clearTimeout(t); w.terminate(); resolve(r); };
    const t = setTimeout(() => finish({ status: "hang", ms: hangMs, detail: "timeout" }), hangMs);
    // message arrives as unknown over the channel; the worker only ever posts a Verdict.
    w.on("message", (m) => finish(m as Verdict));
    w.on("error", (e) => finish({ status: "throw", ms: -1, detail: "worker error: " + (e as Error).message }));
    w.postMessage(input);
  });
}

// import { renderMarkdown } in a buggy module via a file:// path. We re-export the
// real renderer and wrap it so each bug is a one-line perturbation.
const realUrl = JSON.stringify("file://" + join(HERE, "lib", "render.ts"));
const domUrl = JSON.stringify("file://" + join(HERE, "lib", "dom-shim.ts"));

const BUGGY = {
  // 1. throws on a marker input
  bug_throw: `
    import { renderMarkdown as real } from ${realUrl};
    export const renderMarkdown = (s) => { if (String(s).includes("BOOM")) throw new Error("seeded boom"); return real(s); };
  `,
  // 2. infinite loop on a marker input (tests the watchdog hang path)
  bug_hang: `
    import { renderMarkdown as real } from ${realUrl};
    export const renderMarkdown = (s) => { if (String(s).includes("LOOP")) { while (true) {} } return real(s); };
  `,
  // 3. F4 bypass: forge a live <a href="javascript:..."> via the shim (bypassing sanitizeUrl)
  bug_security: `
    import { renderMarkdown as real } from ${realUrl};
    import { installDom } from ${domUrl};
    installDom();
    export const renderMarkdown = (s) => {
      const f = real(s);
      if (String(s).includes("XSS")) { const a = document.createElement("a"); a.setAttribute("href", "javascript:alert(1)"); f.appendChild(a); }
      return f;
    };
  `,
  // 4. nondeterminism: output depends on a module-level counter
  bug_nondet: `
    import { renderMarkdown as real } from ${realUrl};
    import { installDom } from ${domUrl};
    installDom();
    let n = 0;
    export const renderMarkdown = (s) => { const f = real(s); if (String(s).includes("FLAKY")) { const p = document.createElement("p"); p.textContent = String(n++); f.appendChild(p); } return f; };
  `,
  // 5. unbounded: emit a huge subtree for a tiny input
  bug_unbounded: `
    import { renderMarkdown as real } from ${realUrl};
    import { installDom } from ${domUrl};
    installDom();
    export const renderMarkdown = (s) => { const f = real(s); if (String(s).includes("BLOW")) { for (let i=0;i<500000;i++) f.appendChild(document.createTextNode("x")); } return f; };
  `,
};

function writeBuggy(name: string, code: string) { const p = join(TMP, name + ".ts"); writeFileSync(p, code); return "file://" + p; }

// each expectation names a seeded buggy renderer (a BUGGY key) and the verdict it must produce.
interface Expectation { name: keyof typeof BUGGY; input: string; want: string; detailHas?: string; }
const EXPECT: Expectation[] = [
  { name: "bug_throw", input: "hello BOOM world", want: "throw" },
  { name: "bug_hang", input: "please LOOP forever", want: "hang" },
  { name: "bug_security", input: "trigger XSS now", want: "violation", detailHas: "SECURITY" },
  { name: "bug_nondet", input: "this is FLAKY", want: "violation", detailHas: "NONDETERMINISM" },
  { name: "bug_unbounded", input: "make it BLOW up", want: "violation", detailHas: "BOUNDS" },
];

let pass = 0, fail = 0;
console.log("=== selftest: harness must CATCH seeded bugs ===");
for (const e of EXPECT) {
  const mod = writeBuggy(e.name, BUGGY[e.name]);
  const r = await runWith(mod, e.input);
  const ok = r.status === e.want && (!e.detailHas || String(r.detail).includes(e.detailHas));
  console.log(`${ok ? "PASS" : "FAIL"}  ${e.name}: got status=${r.status} ${e.detailHas ? `detail~="${e.detailHas}"` : ""}  ${ok ? "" : "(detail: " + String(r.detail).slice(0, 120) + ")"}`);
  ok ? pass++ : fail++;
}

console.log("\n=== selftest: REAL renderer must PASS a known-good battery ===");
const realMod = "file://" + join(HERE, "lib", "render.ts");
const GOOD = [
  "# h *i* `c`", "- a\n- b", "> q\n> > r", "```python\ndef f():pass\n```",
  "$x^2$ and $$\\sum k$$", "| a | b |\n|-|-|\n|1|2|", "[x](https://y.com) ![i](data:image/png;base64,A)",
  "<script>x</script>", "text with $5 and $10 currency",
];
for (const g of GOOD) {
  const r = await runWith(realMod, g);
  const ok = r.status === "ok";
  console.log(`${ok ? "PASS" : "FAIL"}  real: ${JSON.stringify(g).slice(0, 50)}  -> ${r.status}${ok ? "" : " (" + String(r.detail).slice(0, 100) + ")"}`);
  ok ? pass++ : fail++;
}

rmSync(TMP, { recursive: true, force: true });
console.log(`\n${fail === 0 ? "ALL GOOD" : "SELFTEST FAILED"}: ${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
