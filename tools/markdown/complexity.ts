// complexity.ts - systematic SUPER-LINEAR growth probe (ReDoS + amplification).
//
// fuzz.ts uses a per-input time threshold, which catches a SLOW input but not a
// pathological GROWTH RATE: a quadratic that is 2ms at 8KB looks fine until it is
// 40s at 1MB. This probe renders each pattern across geometric input sizes and
// fits the growth exponent (log-log slope of time vs size) AND the node-count
// amplification (DOM nodes per source byte). It flags:
//   * TIME super-linear   - exponent >~1.5 (linear~1.0, quadratic~2.0)
//   * NODE amplification   - nodes/byte that grows with input size (O(n^k) DOM)
//
// It is the standing guard against algorithmic-complexity regressions (ReDoS,
// O(n^2) amplification) - the failure modes a per-input threshold cannot see.
// Run: node complexity.ts
// Exit 0 = everything linear/bounded; exit 1 = a super-linear pattern found.

import { renderMarkdown } from "./lib/render.ts";
import { elements } from "./lib/oracle.ts";

const BUDGET_MS = 4000; // stop growing a pattern once a single render exceeds this

// a pattern generator maps a target size to a markdown string of ~that scale.
type Gen = (s: number) => string;
// one measured render: source length, wall ms (-1 == threw), DOM node count (-1 == threw).
interface Point { s: number; ms: number; nodes: number; err?: string; }

// each pattern: name -> generator(size) producing a markdown string of ~size scale
const PATTERNS: Record<string, Gen> = {
  // --- suspected/known problem patterns ---
  "table KxM (cols=rows)": (s) => { const k = Math.round(Math.sqrt(s)); const h = "|" + "a|".repeat(k); const d = "|" + "-|".repeat(k); return [h, d, ...Array(k).fill("|x|")].join("\n"); },
  "highlight cpp /*a (repeated opener)": (s) => "```cpp\n" + "/*a".repeat(Math.round(s / 3)) + "\n```",
  "highlight js /*a (repeated opener)": (s) => "```javascript\n" + "/*a".repeat(Math.round(s / 3)) + "\n```",
  "highlight cpp R\"x(a (raw string)": (s) => "```cpp\n" + 'R"x(a'.repeat(Math.round(s / 5)) + "\n```",
  "highlight rust r#\"a (raw string)": (s) => "```rust\n" + 'r#"a'.repeat(Math.round(s / 4)) + "\n```",
  // --- baselines that MUST stay linear/bounded (sanity for the probe) ---
  "inline $ wall (bounded scan)": (s) => "$".repeat(s),
  "inline ] wall (bounded scan)": (s) => "](".repeat(Math.round(s / 2)),
  "emphasis * wall (capped nest)": (s) => "*".repeat(s),
  "blockquote > nesting (MAX_NEST)": (s) => Array.from({ length: Math.min(s, 5000) }, (_, i) => ">".repeat(i + 1) + "x").join("\n"),
  "plain paragraphs (linear)": (s) => Array.from({ length: Math.round(s / 10) }, () => "lorem ipsum dolor sit amet").join("\n\n"),
};

const SIZES = [4000, 8000, 16000, 32000, 64000, 128000];

function measure(gen: Gen): Point[] {
  const pts: Point[] = [];
  for (const s of SIZES) {
    const src = gen(s);
    const t0 = process.hrtime.bigint();
    let frag;
    try { frag = renderMarkdown(src); } catch (e) { pts.push({ s: src.length, ms: -1, nodes: -1, err: String(e).slice(0, 40) }); break; }
    const ms = Number(process.hrtime.bigint() - t0) / 1e6;
    pts.push({ s: src.length, ms, nodes: elements(frag).length });
    if (ms > BUDGET_MS) break; // do not blow the wall clock proving a known quadratic
  }
  return pts;
}

// log-log slope between the first reliable (>1ms) point and the last point.
function exponent(pts: Point[]): { exp: number; note: string } {
  const good = pts.filter((p) => p.ms > 1 && p.s > 0);
  if (good.length < 2) return { exp: 0, note: "too fast to fit" };
  // length >= 2 from the guard above => both ends are present.
  const a = good[0]!, b = good[good.length - 1]!;
  const exp = Math.log(b.ms / a.ms) / Math.log(b.s / a.s);
  return { exp, note: "" };
}
function amplification(pts: Point[]): { grows: boolean; maxPerByte: number } {
  const good = pts.filter((p) => p.nodes > 0);
  if (good.length < 2) return { grows: false, maxPerByte: 0 };
  // length >= 2 from the guard above => both ends are present.
  const first = good[0]!.nodes / good[0]!.s, last = good[good.length - 1]!.nodes / good[good.length - 1]!.s;
  return { grows: last > first * 1.8, maxPerByte: last };
}

let flagged = 0;
console.log("=== complexity probe (time exponent: ~1 linear, ~2 quadratic) ===\n");
for (const [name, gen] of Object.entries(PATTERNS)) {
  const pts = measure(gen);
  const { exp } = exponent(pts);
  const amp = amplification(pts);
  // every pattern produces >= 1 point (the first SIZES render always runs), so last is defined.
  const last = pts[pts.length - 1]!;
  const timeBad = exp > 1.5;
  const ampBad = amp.grows && amp.maxPerByte > 20;
  const bad = timeBad || ampBad || pts.some((p) => p.ms < 0);
  if (bad) flagged++;
  const tag = bad ? "FLAG" : "ok  ";
  const why = [timeBad ? `time^${exp.toFixed(2)}` : "", ampBad ? `amp ${amp.maxPerByte.toFixed(0)}/byte(growing)` : "", pts.some((p) => p.ms < 0) ? "THREW" : ""].filter(Boolean).join(", ");
  console.log(`${tag} ${name.padEnd(38)} exp=${exp.toFixed(2)}  ${last.ms < 0 ? "THREW" : last.ms.toFixed(0) + "ms@" + (last.s / 1000).toFixed(0) + "k"}  ${last.nodes > 0 ? (last.nodes / last.s).toFixed(1) + " nodes/byte" : ""}  ${why ? "<- " + why : ""}`);
}
console.log(`\n${flagged === 0 ? "ALL LINEAR/BOUNDED" : flagged + " SUPER-LINEAR pattern(s) flagged"}.`);
process.exit(flagged ? 1 : 0);
