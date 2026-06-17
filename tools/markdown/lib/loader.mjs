// loader.mjs - a Node ESM resolve hook so the REAL renderer loads UNMODIFIED.
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
// Registered with: node --import ./tools/markdown/lib/loader-register.mjs <entry>
// (or programmatically via module.register from loader-register.mjs).

import { pathToFileURL } from "node:url";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const HERE = dirname(fileURLToPath(import.meta.url));
// tools/markdown/lib -> repo root -> wwwroot
const WWWROOT = join(HERE, "..", "..", "..", "src", "Gert.Api", "wwwroot");

export async function resolve(specifier, context, nextResolve) {
  if (specifier.startsWith("/")) {
    const abs = join(WWWROOT, specifier.slice(1));
    return { url: pathToFileURL(abs).href, shortCircuit: true };
  }
  return nextResolve(specifier, context);
}

export const WWWROOT_DIR = WWWROOT;
