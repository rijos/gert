// check.mjs - the MANUAL corpus runner + regression battery. Renders every
// corpus/*.md through the REAL renderer and asserts the oracle contracts
// (total / secure / bounded / well-formed / deterministic). Unlike the fuzzer
// this is curated and deterministic, so it makes a fast CI-style gate and a place
// to pin specific adversarial cases (the security-F4 file in particular asserts
// that known attacks are NEUTRALIZED, not merely "don't crash").
//
// Run: node check.mjs   (exit 0 = all corpus cases satisfy every contract)

import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { readdirSync, readFileSync } from "node:fs";
import { renderMarkdown } from "./lib/render.mjs";
import { checkSecurity, checkBounds, checkWellFormed, serialize, elements } from "./lib/oracle.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const CORPUS = join(HERE, "corpus");

function contracts(src) {
  const out = [];
  let frag;
  try { frag = renderMarkdown(src); }
  catch (e) { return [`THREW: ${(e && e.message) || e}`]; }
  if (!frag || frag.nodeType !== 11) out.push("did not return a DocumentFragment");
  const sec = checkSecurity(frag); if (sec) out.push("SECURITY: " + sec.join(" | "));
  const wf = checkWellFormed(frag); if (wf) out.push("WELLFORMED: " + wf);
  const bnd = checkBounds(frag, src.length); if (bnd) out.push("BOUNDS: " + bnd);
  let frag2;
  try { frag2 = renderMarkdown(src); } catch (e) { out.push("NONDET: 2nd render threw " + e); }
  if (frag2 && serialize(frag) !== serialize(frag2)) out.push("NONDET: outputs differ");
  return out;
}

// Targeted assertions for the security file: confirm specific neutralizations,
// not just "no oracle violation". (The oracle already proves the general stance;
// these pin the exact behaviours docs/design/security.md F4 promises.)
function securityAssertions(src) {
  const frag = renderMarkdown(src);
  const els = elements(frag);
  const fails = [];
  const tags = els.map((e) => e.localName.toLowerCase());
  if (tags.includes("script")) fails.push("a live <script> element was built");
  if (els.some((e) => (e.attributes || []).some((a) => a.name.toLowerCase().startsWith("on")))) fails.push("an on* handler attribute survived");
  for (const a of els.filter((e) => e.localName === "a")) {
    const href = a.getAttribute("href") || "";
    if (/^\s*(javascript|data|vbscript):/i.test(href)) fails.push("dangerous href survived: " + href);
  }
  for (const img of els.filter((e) => e.localName === "img")) {
    const src2 = img.getAttribute("src") || "";
    if (src2 !== "#" && !/^data:image\/(png|jpe?g|gif|webp|avif|bmp|x-icon);base64,/i.test(src2)) fails.push("non-data img src survived: " + src2);
  }
  return fails;
}

// Totality on HOSTILE / non-string inputs: normalization lives inside the facade
// try/catch, so even a throwing toString() cannot escape renderMarkdown. These
// cannot be expressed as .md corpus files, so they are pinned here.
function totalityBattery() {
  const hostile = [
    ["throwing toString", { toString() { throw new Error("evil"); } }],
    ["throwing valueOf", { valueOf() { throw new Error("evil"); }, toString() { throw new Error("evil"); } }],
    ["Symbol", Symbol("x")],
    ["BigInt", 10n],
    ["null", null],
    ["undefined", undefined],
    ["number", 42],
    ["array", ["a", "b"]],
  ];
  const fails = [];
  for (const [label, input] of hostile) {
    try { const f = renderMarkdown(input); if (!f || f.nodeType !== 11) fails.push(`${label}: not a fragment`); }
    catch (e) { fails.push(`${label}: THREW ${(e && e.message) || e}`); }
  }
  return fails;
}

const files = readdirSync(CORPUS).filter((f) => f.endsWith(".md")).sort();
let failed = 0, totalEls = 0;
console.log("=== totality on hostile/non-string inputs (F3 guard) ===");
const tb = totalityBattery();
if (tb.length) { failed++; console.log("FAIL  totality battery"); for (const v of tb) console.log(`        - ${v}`); }
else console.log("ok    8 hostile inputs all return a fragment (renderMarkdown is TOTAL)");
console.log(`\n=== corpus check: ${files.length} files ===`);
for (const f of files) {
  const src = readFileSync(join(CORPUS, f), "utf8");
  const viol = contracts(src);
  let extra = [];
  if (f.includes("security")) extra = securityAssertions(src);
  const els = (() => { try { return elements(renderMarkdown(src)).length; } catch { return -1; } })();
  totalEls += Math.max(0, els);
  const all = viol.concat(extra);
  if (all.length) { failed++; console.log(`FAIL  ${f}`); for (const v of all) console.log(`        - ${v}`); }
  else console.log(`ok    ${f}  (${els} elements)`);
}
console.log(`\n${failed === 0 ? "PASS" : "FAIL"}: ${files.length - failed}/${files.length} files satisfy every contract (${totalEls} elements total)`);
process.exit(failed ? 1 : 0);
