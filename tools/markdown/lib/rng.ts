// rng.ts - a tiny seeded PRNG so every fuzz run is REPRODUCIBLE. A failing case
// is fully described by its (seed, index): re-running with the same seed replays
// the exact same input stream. No Math.random anywhere in the fuzzer.
//
// mulberry32: 32-bit state, fast, good enough for generation/mutation (this is a
// fuzzer, not a CSPRNG). splitmix32 seeds it from a string/number.

// A seed is a string or number; hashSeed folds either into a 32-bit state word.
export type Seed = string | number;
// [item, weight] pairs consumed by weighted(); generic over the item type.
export type WeightedPair<T> = readonly [T, number];

function hashSeed(seed: Seed): number {
  if (typeof seed === "number") return seed >>> 0;
  const s = String(seed);
  let h = 2166136261 >>> 0;
  for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619); }
  return h >>> 0;
}

export class Rng {
  readonly seed: Seed;
  state: number;
  constructor(seed: Seed) {
    this.seed = seed;
    this.state = hashSeed(seed) || 0x9e3779b9;
  }
  // next float in [0,1)
  float(): number {
    let t = (this.state += 0x6d2b79f5);
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  }
  // int in [0,n)
  int(n: number): number { return Math.floor(this.float() * n); }
  // int in [lo,hi]
  range(lo: number, hi: number): number { return lo + this.int(hi - lo + 1); }
  // probability p (0..1)
  chance(p: number): boolean { return this.float() < p; }
  // pick one element. Callers only feed non-empty literal arrays, so the indexed
  // element is present; the cast restores the pre-noUncheckedIndexedAccess type.
  pick<T>(arr: readonly T[]): T { return arr[this.int(arr.length)] as T; }
  // pick weighted: [[item, weight], ...]
  weighted<T>(pairs: ReadonlyArray<WeightedPair<T>>): T {
    let total = 0; for (const [, w] of pairs) total += w;
    let r = this.float() * total;
    for (const [item, w] of pairs) { if ((r -= w) < 0) return item; }
    // pairs is always non-empty at the call sites; assert the final element exists.
    return pairs[pairs.length - 1]![0];
  }
  // repeat a generator k times
  times<T>(k: number, fn: (i: number) => T): T[] { const out: T[] = []; for (let i = 0; i < k; i++) out.push(fn(i)); return out; }
}

export const rngFor = (seed: Seed): Rng => new Rng(seed);
