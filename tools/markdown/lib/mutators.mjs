// mutators.mjs - structural + byte-level mutation operators. Mutation finds the
// bugs grammar generation misses: it takes a known-interesting seed (corpus case
// or a prior generation) and perturbs it, so near-valid-but-broken inputs (the
// half-open fence, the off-by-one delimiter wall, the smuggled control char) get
// explored densely around real structure. Pure function of (rng, input).

// Codepoints that historically break naive renderers: NUL, controls, the BOM,
// zero-width + bidi + combining marks, lone-surrogate-shaped escapes, astral.
const INTERESTING = [
  "\x00", "\x01", "\x07", "\x08", "\x0b", "\x0c", "\x1b", "\x7f",
  "﻿", "​", "‍", "‎", "‮", "́", "҈",
  "\uD800", "\uDFFF", "�", "\u{1F600}", "\u{10FFFF}",
  "`", "*", "_", "~", "$", "\\", "[", "]", "(", ")", "<", ">", "&", "|", "#", "\n", "\t", "  \n",
];
const SNIPPETS = ["```", "~~~", "$$", "\\[", "\\]", "> ", "- [ ] ", "| - |", "](", "![", "&#x", "<script>", "R\"x(", "@\"\"\""];

function clamp(s, max = 16000) { return s.length > max ? s.slice(0, max) : s; }

const OPS = [
  // insert an interesting char/snippet
  (rng, s) => { const p = rng.int(s.length + 1); const t = rng.chance(0.5) ? rng.pick(INTERESTING) : rng.pick(SNIPPETS); return s.slice(0, p) + t + s.slice(p); },
  // delete a span
  (rng, s) => { if (!s.length) return s; const a = rng.int(s.length); const b = rng.range(a, Math.min(s.length, a + rng.range(1, 20))); return s.slice(0, a) + s.slice(b); },
  // duplicate a span (grows walls)
  (rng, s) => { if (!s.length) return s; const a = rng.int(s.length); const b = rng.range(a, Math.min(s.length, a + rng.range(1, 40))); const seg = s.slice(a, b); const k = rng.range(2, 12); return clamp(s.slice(0, b) + seg.repeat(k) + s.slice(b)); },
  // char flip to an interesting char
  (rng, s) => { if (!s.length) return s; const p = rng.int(s.length); return s.slice(0, p) + rng.pick(INTERESTING) + s.slice(p + 1); },
  // repeat a single char into a wall
  (rng, s) => { if (!s.length) return s; const p = rng.int(s.length); const ch = rng.pick(["*", "_", "~", "`", "$", "[", "#", ">", "(", ")", "\\", '"', "#"]); return clamp(s.slice(0, p) + ch.repeat(rng.range(20, 400)) + s.slice(p)); },
  // truncate (half-open everything)
  (rng, s) => s.slice(0, rng.int(s.length + 1)),
  // transpose two spans
  (rng, s) => { if (s.length < 4) return s; const a = rng.int(s.length >> 1); const b = rng.range(s.length >> 1, s.length); return s.slice(b) + s.slice(a, b) + s.slice(0, a); },
  // strip newlines (jam blocks together)
  (rng, s) => rng.chance(0.5) ? s.replace(/\n/g, " ") : s.replace(/ /g, "\n"),
];

// mutate(rng, input, rounds) -> a mutated string (1..rounds operators applied).
export function mutate(rng, input, rounds = 0) {
  let s = String(input ?? "");
  const k = rounds || rng.range(1, 4);
  for (let i = 0; i < k; i++) s = OPS[rng.int(OPS.length)](rng, s);
  return clamp(s);
}

// splice(rng, a, b) -> a crossover of two seeds.
export function splice(rng, a, b) {
  const i = rng.int(a.length + 1), j = rng.int(b.length + 1);
  return clamp(a.slice(0, i) + b.slice(j));
}

export { INTERESTING, SNIPPETS };
