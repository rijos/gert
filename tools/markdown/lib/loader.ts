// loader.ts - a Node ESM resolve hook so the REAL renderer loads UNMODIFIED.
//
// The renderer's only non-relative imports are two absolute, server-root paths
// inside render/dom.js:
//   import { MdMath } from "/components/canvas/artifacts/md-math.js";
//   import { MdCode } from "/components/canvas/artifacts/md-code.js";
// In the browser the server roots "/" at wwwroot; in Node a leading "/" is a
// filesystem-absolute path that would not exist. This hook rewrites any specifier
// beginning with "/" to the corresponding file under wwwroot, so we run the exact
// bytes shipped to the browser - no edits, no stubs, real smath + highlight.
//
// Registered with: node --import ./tools/markdown/lib/loader-register.ts <entry>
// (or programmatically via module.register from loader-register.ts).

import { pathToFileURL } from "node:url";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const HERE = dirname(fileURLToPath(import.meta.url));
// tools/markdown/lib -> repo root -> wwwroot. GERT_WWWROOT overrides it so the harness can
// target the esbuild TRANSPILED mirror (the .ts renderer's emitted .js) instead of the source
// tree - which is how the fuzzer validates the post-TypeScript renderer (the source is .ts and
// Node loads .js). Defaults to the source tree (pre-migration, still .js).
const WWWROOT = process.env.GERT_WWWROOT
  ? fileURLToPath(pathToFileURL(process.env.GERT_WWWROOT))
  : join(HERE, "..", "..", "..", "src", "Gert.Api", "wwwroot");

// Minimal shape of the Node ESM resolve-hook contract (only the fields this hook
// touches). `context` is opaque and passed straight through to the next hook.
type ResolveContext = unknown;
interface ResolveResult {
  url: string;
  shortCircuit?: boolean;
}
type NextResolve = (
  specifier: string,
  context: ResolveContext,
) => ResolveResult | Promise<ResolveResult>;

export async function resolve(
  specifier: string,
  context: ResolveContext,
  nextResolve: NextResolve,
): Promise<ResolveResult> {
  if (specifier.startsWith("/")) {
    const abs = join(WWWROOT, specifier.slice(1));
    return { url: pathToFileURL(abs).href, shortCircuit: true };
  }
  return nextResolve(specifier, context);
}

export const WWWROOT_DIR = WWWROOT;
