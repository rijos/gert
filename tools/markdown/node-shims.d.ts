// Minimal ambient declarations for the Node built-ins + globals tools/markdown uses, so tsgo can
// type-check the harness WITHOUT @types/node - keeping the no-npm tenet (the renderer it loads at
// runtime is the real SPA, transpiled to .js, and is not type-checked here). Hand-maintained:
// extend ONLY with the exact surface the harness uses. lib is ES2022 (no DOM - the harness ships
// its own DOM in dom-shim.ts), so the Node globals below are declared here rather than pulled in.
declare module "node:fs" {
  export function readFileSync(path: string, encoding: string): string;
  export function readdirSync(path: string): string[];
  export function mkdirSync(path: string, opts?: { recursive?: boolean }): string | undefined;
  export function writeFileSync(path: string, data: string): void;
  export function rmSync(path: string, opts?: { recursive?: boolean; force?: boolean }): void;
}

declare module "node:path" {
  export function join(...parts: string[]): string;
  export function dirname(p: string): string;
}

declare module "node:url" {
  export function fileURLToPath(url: string | URL): string;
  export function pathToFileURL(path: string): URL;
}

declare module "node:worker_threads" {
  export interface WorkerOptions {
    env?: Record<string, string | undefined>;
    workerData?: unknown;
  }
  export class Worker {
    constructor(filename: string | URL, options?: WorkerOptions);
    on(event: string, listener: (arg: unknown) => void): this;
    once(event: string, listener: (arg: unknown) => void): this;
    off(event: string, listener: (arg: unknown) => void): this;
    postMessage(value: unknown): void;
    terminate(): Promise<number>;
    setMaxListeners(n: number): this;
  }
  export const parentPort: {
    on(event: string, listener: (arg: unknown) => void): void;
    postMessage(value: unknown): void;
  } | null;
}

declare module "node:module" {
  export function register(specifier: string | URL, parentURL: string | URL): void;
}

// --- Node globals (lib ES2022 has none of these) ---
declare const console: {
  log(...args: unknown[]): void;
  error(...args: unknown[]): void;
};
declare const process: {
  argv: string[];
  env: Record<string, string | undefined>;
  pid: number;
  exit(code?: number): never;
  hrtime: { bigint(): bigint };
  stdout: { write(s: string): void; isTTY?: boolean };
};
declare function setTimeout(handler: (...args: unknown[]) => void, ms?: number): number;
declare function clearTimeout(id: number): void;
// ESM module URL; the harness derives __dirname-equivalents via fileURLToPath(import.meta.url).
interface ImportMeta {
  url: string;
}
declare class URL {
  constructor(url: string | URL, base?: string | URL);
  href: string;
  pathname: string;
}
// Installed on globalThis by dom-shim.ts's installDom(); the harness reads globalThis.document.
// Typed as unknown so each reader narrows at its use site rather than leaking an any.
declare var document: unknown;

// The SERVED SPA renderer, loaded at runtime by render.ts via the loader.ts resolve hook
// from the transpiled .js mirror under wwwroot. It is NOT part of this tsgo program, so
// declare the exact surface render.ts statically imports + re-exports. renderMarkdown returns
// the harness's own DOM fragment (a DocumentFragment-shaped node); typed as unknown so each
// reader narrows at its use site (the oracle/worker walk rendered nodes structurally).
//
// nodenext rejects path-like (relative/absolute) ambient module names (TS2436), so each
// served module is matched by a SUFFIX WILDCARD ("*/<file>.js"). The paths render.ts imports
// are unambiguous: "/lib/markdown.js", "/lib/render/{lines,inline,url}.js", "/lib/smath.js".
declare module "*/markdown.js" {
  export function renderMarkdown(src: string): unknown;
  export const sanitizeUrl: unknown;
  export const NODE_TYPES: unknown;
}
declare module "*/lines.js" {
  export function parseBlocks(lines: string[], ctx: unknown, depth: number): unknown[];
}
declare module "*/inline.js" {
  export const parseInline: unknown;
}
declare module "*/url.js" {
  const url: unknown;
  export default url;
}
declare module "*/smath.js" {
  const smath: unknown;
  export default smath;
}
