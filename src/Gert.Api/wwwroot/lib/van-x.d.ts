// van-x.d.ts - the type sidecar for the vendored lib/van-x.js (vanjs-ext 0.6.3).
//
// Copied from vanjs-ext 0.6.3's first-party src/van-x.d.ts; the ONLY adaptation is the import
// source: upstream imports `State` from the "vanjs-core" bare package, but here van's types live
// in the sibling sidecar, so we import from "./van.js" (which resolves to van.d.ts). The SPA only
// uses `reactive`, but the full surface is declared so a future use stays typed. Like van.d.ts,
// this sidecar takes precedence over the ".js" under moduleResolution: Bundler. See
// spa-style-guide.md "TypeScript conventions" (vendored van, typed by sidecar .d.ts).
import type { State } from "./van.js"

export declare const calc: <R>(f: () => R) => R
export declare const reactive: <T extends object>(obj: T) => T
export declare const noreactive: <T extends object>(obj: T) => T

export type StateOf<T> = { readonly [K in keyof T]: State<T[K]> }
export declare const stateFields: <T extends object>(obj: T) => StateOf<T>
export declare const raw: <T extends object>(obj: T) => T

export type ValueType<T> = T extends (infer V)[] ? V : T[keyof T]
export type KeyType<T> = T extends unknown[] ? number : string
export declare const list: <T extends object, ElementType extends Element>
  (container: (() => ElementType) | ElementType, items: T,
  itemFunc: (v: State<ValueType<T>>, deleter: () => void, k: KeyType<T>) => Node) => ElementType

export type ReplacementFunc<T> =
  T extends (infer V)[] ? (items: V[]) => readonly V[] :
  (items: [string, T[keyof T]][]) => readonly [string, T[keyof T]][]
export declare const replace: <T extends object>(obj: T, replacement: ReplacementFunc<T> | T) => T

export declare const compact: <T extends object>(obj: T) => T
