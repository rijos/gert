// render.ts - the headless entry point. Installs the DOM shim, registers the
// "/" -> wwwroot loader hook, then dynamically imports the UNMODIFIED renderer so
// every transitive import (render/*, smath, highlight, component, the MdMath /
// MdCode leaves) resolves to the real browser bytes. Re-exports renderMarkdown
// plus a few pure sub-modules for parser-level fuzzing and the oracle layer.
//
// Top-level await: importing this module transparently waits for the renderer to
// load, so consumers just `import { renderMarkdown } from "./lib/render.ts"`.

import { register } from "node:module";
import { installDom } from "./dom-shim.ts";

installDom();
register("./loader.ts", import.meta.url);

// dynamic, AFTER register() so the hook governs this import and its whole graph.
const md = await import("/lib/markdown.js");

export const renderMarkdown = md.renderMarkdown;
export const sanitizeUrl = md.sanitizeUrl;
export const NODE_TYPES = md.NODE_TYPES;

// Pure (DOM-free) sub-modules, handy for parser-level invariants/fuzzing.
export const lines = await import("/lib/render/lines.js");
export const inline = await import("/lib/render/inline.js");
export const url = await import("/lib/render/url.js");
export const smath = await import("/lib/smath.js");

// parseDocument(src) -> the block AST (the same array renderMarkdown builds from),
// using the renderer's own normalize + parseBlocks + injected parseInline.
export function parseDocument(src: unknown) {
  const source = String(src ?? "").replace(/\r\n?/g, "\n").replace(/\t/g, "    ");
  const ctx = { doc: globalThis.document, parseInline: inline.parseInline };
  return { type: "document", children: lines.parseBlocks(source.split("\n"), ctx, 0) };
}

// import any module under wwwroot by server-root path (e.g. "/lib/highlight.js").
export const importWww = (spec: string) => import(spec.startsWith("/") ? spec : "/" + spec);
