// fuzz.ts - the automatic fuzzer entry point. Generates + mutates markdown,
// renders each input in an ISOLATED worker (lib/worker.ts) under a hard per-input
// timeout, and asserts the renderer's contracts via the oracle:
//
//   total       -> never throws / always returns a fragment   (status 'throw')
//   secure (F4) -> allow-list elements/attrs, scrubbed urls    (status 'violation')
//   bounded     -> node count + depth within ceiling           (status 'violation')
//   well-formed -> acyclic, consistent parent links            (status 'violation')
//   determinist -> identical output across two renders         (status 'violation')
//   terminating -> completes within the timeout / slow budget  (status 'hang'/'slow')
//
// Every input is a pure function of (seed, index), so a failure is fully described
// by its seed; the fuzzer also MINIMIZES each unique failure (lib/minimize.ts)
// and writes a small repro under .fuzz-out/. Reproduce a single seed run with
// --seed <n>. No npm; Node's built-in worker_threads only.
//
// Usage:
//   node fuzz.ts [--seed N] [-n ITERS | --time SECS] [--slow-ms MS]
//                 [--hang-ms MS] [--inline] [--no-mutate] [--quiet]
// Examples:
//   node fuzz.ts -n 5000
//   node fuzz.ts --time 30 --slow-ms 150
//   node fuzz.ts --seed 12345        # replay a specific run

import { Worker } from "node:worker_threads";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { readdirSync, readFileSync, mkdirSync, writeFileSync } from "node:fs";
import { Rng } from "./lib/rng.ts";
import { generate, generateInline } from "./lib/generators.ts";
import { mutate, splice } from "./lib/mutators.ts";
import { minimize } from "./lib/minimize.ts";

// CLI options. seed/iters/time start null and are filled from argv (or defaulted below).
interface Opts {
  seed: number | null; iters: number | null; time: number | null;
  slowMs: number; hangMs: number; inline: boolean; mutate: boolean; quiet: boolean;
}
// The worker's reply protocol (lib/worker.ts). status widens to 'slow' in the parent's grading.
interface Verdict { status: string; ms: number; detail: string; }
// one deduplicated failure the fuzzer minimizes + reports.
interface Failure { sig: string; status: string; detail: string; ms: number; input: string; raw: string; }

const HERE = dirname(fileURLToPath(import.meta.url));
const WORKER = join(HERE, "lib", "worker.ts");
const OUT = join(HERE, ".fuzz-out");

// ---- args -------------------------------------------------------------------
function parseArgs(argv: string[]): Opts {
  const a: Opts = { seed: null, iters: null, time: null, slowMs: 200, hangMs: 3000, inline: false, mutate: true, quiet: false };
  for (let i = 0; i < argv.length; i++) {
    const k = argv[i];
    if (k === "--seed") a.seed = Number(argv[++i]);
    else if (k === "-n" || k === "--iters") a.iters = Number(argv[++i]);
    else if (k === "--time") a.time = Number(argv[++i]);
    else if (k === "--slow-ms") a.slowMs = Number(argv[++i]);
    else if (k === "--hang-ms") a.hangMs = Number(argv[++i]);
    else if (k === "--inline") a.inline = true;
    else if (k === "--no-mutate") a.mutate = false;
    else if (k === "--quiet") a.quiet = true;
  }
  if (a.iters == null && a.time == null) a.iters = 3000;
  return a;
}

// ---- isolated runner with per-input watchdog --------------------------------
class Runner {
  hangMs: number;
  worker!: Worker; // assigned by spawn() in the constructor before any use.
  constructor(hangMs: number) { this.hangMs = hangMs; this.spawn(); }
  spawn() { this.worker = new Worker(WORKER); this.worker.setMaxListeners(50); this.worker.on("error", () => {}); }
  async warmup() { await this.run("# warmup\n\ntext", Math.max(this.hangMs, 8000)); }
  run(input: string, timeoutMs = this.hangMs): Promise<Verdict> {
    return new Promise((resolve) => {
      let done = false;
      // message arrives as unknown over the channel; the worker only ever posts a Verdict.
      const onMsg = (m: unknown) => { if (done) return; done = true; clearTimeout(timer); this.worker.off("message", onMsg); resolve(m as Verdict); };
      const timer = setTimeout(() => {
        if (done) return; done = true; this.worker.off("message", onMsg);
        this.worker.terminate().then(() => this.spawn());
        resolve({ status: "hang", ms: timeoutMs, detail: `no reply within ${timeoutMs}ms (terminated)` });
      }, timeoutMs);
      this.worker.on("message", onMsg);
      this.worker.postMessage(input);
    });
  }
  async close() { try { await this.worker.terminate(); } catch { /* ignore */ } }
}

// ---- failure signature + minimize predicates --------------------------------
function violationKind(detail: string) { return (String(detail).split(":")[0] || "").trim().toUpperCase(); }
// _input is intentionally unused (the signature derives only from the verdict); kept for call-site shape.
function signature(_input: string, res: Verdict): string {
  if (res.status === "throw") {
    const m = /at\s+([^\s(]+)/.exec(res.detail || "");
    // .split("\n") always yields >= 1 element, so [0] is present.
    const msg = (res.detail || "").split("\n")[0]!.slice(0, 80);
    return "throw:" + (m ? m[1] : "") + ":" + msg;
  }
  if (res.status === "violation") return "violation:" + violationKind(res.detail);
  if (res.status === "hang") return "hang";
  if (res.status === "slow") return "slow";
  return "ok";
}
type Predicate = (s: string) => Promise<boolean>;
function predicateFor(runner: Runner, res: Verdict, slowMs: number): Predicate {
  if (res.status === "throw") return async (s) => (await runner.run(s)).status === "throw";
  if (res.status === "violation") { const k = violationKind(res.detail); return async (s) => { const r = await runner.run(s); return r.status === "violation" && violationKind(r.detail) === k; }; }
  if (res.status === "hang") return async (s) => (await runner.run(s)).status === "hang";
  if (res.status === "slow") return async (s) => { const r = await runner.run(s); return (r.status === "ok" || r.status === "hang") && r.ms >= slowMs * 0.7; };
  return async () => false;
}

// ---- corpus seeds (for mutation) -------------------------------------------
function loadCorpus(): string[] {
  try {
    const dir = join(HERE, "corpus");
    return readdirSync(dir).filter((f) => f.endsWith(".md")).map((f) => readFileSync(join(dir, f), "utf8"));
  } catch { return []; }
}

// ---- input source -----------------------------------------------------------
function makeInput(rng: Rng, opts: Opts, corpus: string[], recent: string[]): string {
  const roll = rng.float();
  if (opts.inline) return generateInline(rng);
  if (opts.mutate && roll < 0.25 && corpus.length) return mutate(rng, rng.pick(corpus));
  if (opts.mutate && roll < 0.4 && recent.length) return mutate(rng, rng.pick(recent));
  if (opts.mutate && roll < 0.5 && recent.length && corpus.length) return splice(rng, rng.pick(recent), rng.pick(corpus));
  if (roll < 0.7) return generate(rng, { maxBlocks: rng.range(1, 10), depth: rng.range(1, 4) });
  return generateInline(rng);
}

// ---- main -------------------------------------------------------------------
async function main() {
  const opts = parseArgs(process.argv.slice(2));
  // Date.now() is fine here (the fuzzer is a plain Node script, not a workflow).
  const masterSeed = opts.seed ?? ((Date.now() ^ (process.pid << 8)) >>> 0);
  const rng = new Rng(masterSeed);
  const corpus = loadCorpus();
  const runner = new Runner(opts.hangMs);
  await runner.warmup();

  const seen = new Set<string>();
  const failures: Failure[] = [];
  const recent: string[] = [];
  let count = 0, okMs = 0, maxMs = 0, slowCount = 0;
  const startWall = Date.now();
  const deadline = opts.time != null ? startWall + opts.time * 1000 : Infinity;
  const target = opts.iters ?? Infinity;

  if (!opts.quiet) console.log(`fuzz: masterSeed=${masterSeed} corpus=${corpus.length} ${opts.inline ? "[inline] " : ""}slow=${opts.slowMs}ms hang=${opts.hangMs}ms ${opts.time ? `time=${opts.time}s` : `n=${opts.iters}`}`);

  while (count < target && Date.now() < deadline) {
    count++;
    const input = makeInput(rng, opts, corpus, recent);
    if (recent.length < 40) recent.push(input); else recent[count % 40] = input;

    let res = await runner.run(input);
    if (res.status === "ok") { okMs += res.ms; if (res.ms > maxMs) maxMs = res.ms; if (res.ms >= opts.slowMs) { res = { ...res, status: "slow" }; slowCount++; } }

    if (res.status !== "ok") {
      const sig = signature(input, res);
      if (!seen.has(sig)) {
        seen.add(sig);
        const pred = predicateFor(runner, res, opts.slowMs);
        const small = await minimize(input, pred, { maxRounds: 20 });
        const rec = { sig, status: res.status, detail: (res.detail || "").split("\n").slice(0, 3).join(" "), ms: res.ms, input: small, raw: input };
        failures.push(rec);
        mkdirSync(OUT, { recursive: true });
        const fname = join(OUT, sig.replace(/[^\w.-]+/g, "_").slice(0, 80) + ".md");
        writeFileSync(fname, small);
        if (!opts.quiet) {
          console.log(`\n[FAIL ${res.status}] sig=${sig}`);
          console.log(`  detail: ${rec.detail}`);
          console.log(`  repro (${small.length}B): ${JSON.stringify(small).slice(0, 200)}`);
          console.log(`  saved: ${fname}`);
        }
      }
    }
    if (!opts.quiet && count % 2000 === 0) {
      const line = `  ${count} inputs, ${failures.length} unique failures, ${slowCount} slow, max ${maxMs.toFixed(1)}ms`;
      if (process.stdout.isTTY) process.stdout.write("\r" + line + "   ");
      else console.log(line);
    }
  }

  await runner.close();
  const wall = ((Date.now() - startWall) / 1000).toFixed(1);
  console.log(`\n\n=== fuzz summary ===`);
  console.log(`inputs:        ${count}`);
  console.log(`wall:          ${wall}s`);
  console.log(`avg render:    ${(okMs / Math.max(1, count)).toFixed(3)}ms   max ok: ${maxMs.toFixed(1)}ms`);
  console.log(`slow (>=${opts.slowMs}ms): ${slowCount}`);
  console.log(`unique failures: ${failures.length}`);
  for (const f of failures) console.log(`  - [${f.status}] ${f.sig}  (${f.input.length}B)  ${f.detail}`);
  if (failures.length) console.log(`\nrepros saved under ${OUT}/  (replay a full run with --seed ${masterSeed})`);
  process.exit(failures.filter((f) => f.status !== "slow").length ? 1 : 0);
}

main().catch((e) => { console.error("fuzzer fault:", e); process.exit(2); });
