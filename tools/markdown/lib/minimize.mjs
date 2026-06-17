// minimize.mjs - delta-debugging shrinker. A fuzzer's raw failing input is often
// a 4KB blob; the actionable repro is the 8 characters that actually trip the bug.
// minimize() greedily removes/simplifies while a predicate still fails, so the
// saved repro is small enough to read and turn into a regression test.
//
// predicate(input) -> (Promise of) true if the input STILL exhibits the failure.
// minimize is agnostic to the failure class (throw / oracle violation / slow /
// hang); the runner passes a predicate that re-checks the SAME class. The
// predicate may be async (e.g. driving the isolated worker), so we await it.

export async function minimize(input, predicate, { maxRounds = 30 } = {}) {
  let best = String(input);
  if (!(await predicate(best))) return best; // not actually failing; nothing to do

  let changed = true, rounds = 0;
  while (changed && rounds++ < maxRounds) {
    changed = false;

    // 1) chunk removal at decreasing granularity (ddmin-style)
    for (let chunks = 8; chunks >= 1; chunks = Math.floor(chunks / 2)) {
      const size = Math.ceil(best.length / chunks);
      if (size < 1) break;
      for (let i = 0; i < best.length; i += size) {
        const cand = best.slice(0, i) + best.slice(i + size);
        if (cand !== best && cand.length && (await predicate(cand))) { best = cand; changed = true; break; }
      }
      if (changed) break;
    }
    if (changed) continue;

    // 2) line removal
    const lines = best.split("\n");
    if (lines.length > 1) {
      for (let i = 0; i < lines.length; i++) {
        const cand = lines.slice(0, i).concat(lines.slice(i + 1)).join("\n");
        if (await predicate(cand)) { best = cand; changed = true; break; }
      }
      if (changed) continue;
    }

    // 3) single-char removal (final tightening)
    for (let i = 0; i < best.length; i++) {
      const cand = best.slice(0, i) + best.slice(i + 1);
      if (await predicate(cand)) { best = cand; changed = true; break; }
    }

    // 4) collapse a long run to 1 (walls -> minimal repro length)
    if (!changed) {
      const m = /(.)\1{3,}/.exec(best);
      if (m) {
        const idx = m.index, ch = m[1];
        let end = idx; while (best[end] === ch) end++;
        for (const keep of [1, 2, 3]) {
          if (keep >= end - idx) continue;
          const cand = best.slice(0, idx) + ch.repeat(keep) + best.slice(end);
          if (await predicate(cand)) { best = cand; changed = true; break; }
        }
      }
    }
  }
  return best;
}
