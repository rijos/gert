// proto/compare.ts - A/B the per-character block scanner (proto/block-scan.ts)
// against the PRODUCTION per-line block parser. Both ASTs go through the SAME
// production renderer + the SAME inline parser, so any output difference is purely
// the block-segmentation strategy. Run: node proto/compare.ts
//
// Expectation: on the constructs the prototype implements (heading / paragraph /
// fenced code / thematic break / blockquote) it is byte-identical to production --
// proving per-character + regex-free block parsing is feasible. On lists/tables
// (which the prototype omits on purpose) it diverges -- proving those boundaries
// genuinely want per-line + lookahead, which is the conclusion here.

import { parseDocument, inline, importWww } from "../lib/render.ts";
import { serialize } from "../lib/oracle.ts";
import { parseBlocksPerChar } from "./block-scan.ts";

const dom = await importWww("/lib/render/dom.js");
const ctx = { doc: globalThis.document, parseInline: inline.parseInline };

// The injected production inline parser. render.ts re-exports `inline` from the SERVED
// "/lib/render/inline.js" module, whose ambient declaration types `parseInline` as `unknown`
// (the served renderer is not part of this tsgo program). parseBlocksPerChar's contract for it
// is block-scan.ts's `ParseInline`; narrow at this dynamic boundary (same value, behavior-identical).
type ParseInline = (text: string, ctx: { parseInline: ParseInline }, depth: number) => readonly unknown[];
const parseInline = inline.parseInline as ParseInline;

function renderAst(children: readonly unknown[]) { return serialize(dom.render({ type: "document", children }, ctx)); }
function prod(src: string) { return renderAst(parseDocument(src).children); }
function proto(src: string) { return renderAst(parseBlocksPerChar(src, parseInline)); }

// SUPPORTED: constructs the prototype implements -> must match production exactly.
const SUPPORTED = [
  "# Heading one",
  "### Heading *three* with `code`",
  "###### \n", // bare h6
  "A paragraph of text\nwrapped over two lines.",
  "Para one.\n\nPara two.",
  "---",
  "***",
  "```python\ndef f(x):\n    return x + 1\n```",
  "```\nplain fence\n```",
  "> a quote\n> second line",
  "> # quoted heading\n>\n> body",
  "para\n\n> quote\n\n# h",
  "Heading then text\n# H\nmore text",
];

// OUT-OF-SCOPE: constructs the prototype deliberately omits -> divergence is the
// POINT (these are the per-line/lookahead-defined boundaries).
const OUT_OF_SCOPE = [
  "- a\n- b\n- c",
  "1. one\n2. two",
  "| a | b |\n| - | - |\n| 1 | 2 |",
  "Setext H1\n=========",
  "    indented code block",
];

let agree = 0, disagree = 0;
console.log("=== SUPPORTED constructs: per-char prototype must equal production ===");
for (const s of SUPPORTED) {
  const a = prod(s), b = proto(s);
  const ok = a === b;
  ok ? agree++ : disagree++;
  console.log(`${ok ? "MATCH" : "DIFF "}  ${JSON.stringify(s).slice(0, 48)}`);
  if (!ok) { console.log("   prod : " + a.slice(0, 160)); console.log("   proto: " + b.slice(0, 160)); }
}

console.log("\n=== OUT-OF-SCOPE constructs: divergence expected (the per-line cliff) ===");
for (const s of OUT_OF_SCOPE) {
  const a = prod(s), b = proto(s);
  const same = a === b;
  console.log(`${same ? "match" : "diverges"}  ${JSON.stringify(s).slice(0, 48)}`);
  if (!same) console.log(`   proto falls back to paragraph: ${b.slice(0, 90)}`);
}

console.log(`\nSupported: ${agree} match / ${disagree} diff.`);
console.log(disagree === 0
  ? "=> Per-character, regex-free block parsing is FEASIBLE and byte-identical on the\n   line-uniform constructs. The divergence on lists/tables/setext is exactly where\n   Markdown's block boundaries are line+lookahead defined -> per-line stays simpler.\n   See README section 3."
  : "=> Unexpected diff among SUPPORTED constructs; investigate proto/block-scan.ts.");
process.exit(disagree === 0 ? 0 : 1);
