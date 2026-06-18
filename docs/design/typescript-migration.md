# TypeScript migration — the SPA on a native Go toolchain

How the **VanJS SPA** (`src/Gert.Api/wwwroot`) moves from native JavaScript ES modules to
**TypeScript** while keeping Gert's **no-npm, no-Node** rule intact. The checker is TypeScript 7's
native Go port (`tsgo`); the transpiler/bundler is the **esbuild Go binary we already vendor**
([ui-components section 6](ui-components.md#6-devrelease-pipeline-no-npm)). This doc is the **plan
and rationale**; once executed, fold the steady-state facts into [ui-components.md](ui-components.md)
and [spa-style-guide.md](spa-style-guide.md) and demote this to a record.

> **One-line architecture:** source becomes `.ts`; **esbuild** transpiles `.ts` → `.js` into a
> served **build dir** (dev) and bundles `app.ts` → `app.js` (release); **tsgo** is a checker only
> (`--noEmit`), run as a fail-closed gate. Both binaries are SHA-512-pinned npm-registry tarballs —
> still **no npm, no Node, zero external runtime deps**.

**Status:** plan — not yet executed.

---

## 1. Why, and what was decided

The SPA is **82 native ES modules, ~8.8 KLoC, zero TypeScript**, served raw in dev with **no build
step**. We want compile-time type safety (nullability, unused-symbol, import-correctness) without
introducing a package manager. The verified path: TypeScript's native Go checker `tsgo` plus the
esbuild we already drive on publish.

| Decision | Choice | Why |
|----------|--------|-----|
| Scope | **Full `.ts` migration** of all app modules | Not a JSDoc gate, not a pilot — types live in the source. |
| Checker | **`tsgo`** (TypeScript 7 native), vendored SHA-512-pinned, **fail-closed CI gate** | Same no-npm provisioning pattern as esbuild; a real gate, not advisory. |
| Transpiler/bundler | **esbuild** (already vendored) | Transpiles `.ts` natively (dev) and bundles `app.ts` → `app.js` (release). Does **not** type-check — that's `tsgo`'s job. |
| Imports | **Unchanged** — keep `.js` specifiers | Every import already ends in `.js` (`from "/lib/van.js"`), which is correct under `moduleResolution: Bundler`. No import string is touched. |
| Vendored van | **Stays `.js`** + sidecar `.d.ts` | Don't fight the minified upstream; type via vanjs-core's first-party `van.d.ts`. |

### What actually constrains the design

The browser is **not** the only consumer of emitted module URLs: the smoke suite imports app
modules by their absolute `.js` URL at runtime (`tools/smoke/tests/test_components.py`:
`await import('/components/main/composer.js')`), the in-repo harness (`tests/web/harness.js`)
resolves modules by absolute same-origin path, and the running app's own source imports `/lib/*.js`,
`/components/*.js`, etc. But all of these are **URL paths, not filesystem paths** — they only require
the static host to serve `.js` at the canonical URLs, independent of where the `.js` physically
lives. No `.js` extension changes anywhere, and **no smoke-test assertion needs to change.**

The lever is therefore the *served root*, not the source tree — and the smoke runner already has it:
`_boot_host(web_root=...)` sets `ASPNETCORE_WEBROOT`, and `_prepare_bundled_webroot()` already
`copytree`s `wwwroot` → builds → serves the copy (used today by `serve-mock --minify`). So the build
can be made **inline in the smoke tool**: serve a *built* webroot rather than littering `wwwroot`
with emitted `.js`. This selects the dev design in
[section 3](#3-dev-serving--transpile-into-a-built-webroot).

---

## 2. Verified toolchain facts

Confirmed empirically in a linux-x64 sandbox on 2026-06-18.

| Component | Version | Notes |
|---|---|---|
| `vanjs-core` | 1.6.0 | Ships first-party `van.d.ts`; sound types; no `@types` needed. |
| `esbuild` (Go ELF) | 0.28.1 | Already vendored. Bundles **and** transpiles `.ts`; **does not** type-check. |
| `tsgo` (Go ELF) | `7.0.0-dev.YYYYMMDD.N` | TS 7 native checker. **Pre-release, daily-dev.** Resolves bare + relative imports under `bundler`/`nodenext`. |

**`tsgo` gotcha:** it loads its sibling `lib.*.d.ts` from the directory containing the binary —
relocate the binary without its `lib/` and it panics. ⇒ extract the whole subtree, invoke in place.

Both binaries are obtainable without npm by fetching the npm-registry `.tgz` over HTTPS and
verifying SHA-512 — the exact mechanism `Gert.Web.Bundle` already uses for esbuild
(`EsbuildBinary` / `EsbuildManifest`).

---

## 3. Dev serving — transpile into a built webroot

`wwwroot/` stays **source-only**: `.ts` modules + `.css`/`.html`/`favicon.svg`/`icons` + the two
vendored van `.js`. It is fully tracked and pristine — no emitted `.js` ever lands in it. What's
*served* in dev is a built mirror, reached through the static-host knob the smoke runner already
drives (`ASPNETCORE_WEBROOT`).

- **Build step** (a `--transpile` variant of `_prepare_bundled_webroot`): `copytree(wwwroot)` → temp
  dir (assets ride along) → esbuild transpiles each `.ts` → sibling `.js` (inline sourcemaps) →
  remove the `.ts` from the copy. esbuild invocation (no `--bundle`):
  `esbuild <files> --outdir=<built> --outbase=<src> --format=esm --sourcemap=inline [--watch]`.
  Enumerate `*.ts` in C# (`Directory.EnumerateFiles(..., AllDirectories)`) and pass explicit entry
  points — shell-glob-independent, matching the tool's existing `ArgumentList` style.
- **Boot:** host runs with `ASPNETCORE_WEBROOT=<built>` via the existing `_boot_host(web_root=...)`
  path. The app's `/lib/*.js` and the tests' `/components/*.js` both resolve from the built mirror —
  **zero test changes**, and `script-src 'self'` is unaffected (served modules are same-origin
  files, no inline script). The `tests/web` harness provider (`RequestPath="/tests"`) is untouched;
  its pages import `/lib/*.js`, which the built webroot serves.
- **Hot reload:** esbuild `--watch` re-transpiles `.ts` → built dir on save (real names + line
  numbers via inline maps). Non-`.js` asset edits (`.css`/`.html`) need a re-copy to appear — a watch
  sync, or just a dev restart. This is the one ergonomic cost vs. serving `wwwroot` raw.
- **Wiring — transpile is the default for every host-boot target.** `make run`, `make dev`,
  `make serve-mock` (default), and the `e2e`/`api-auth` runners all build the transpiled mirror and
  boot against it (`ASPNETCORE_WEBROOT=<built>`). `serve-mock MINIFY=1` is the only variant that
  *bundles* instead (it exercises the published `app.js`/`app.css`). Add `make transpile` (one-shot
  build of the mirror) for priming/CI.

**Rejected:** sibling-`.js`-in-`wwwroot` emit (litters the tree with gitignored build output beside
source — broad `wwwroot/**/*.js` ignore risks swallowing real files, cf. the past `artifacts/`
mis-ignore) and transpile-on-request middleware (a new request path in a security-sensitive host).
The built-mirror approach keeps the source tree clean, reuses an existing, proven mechanism, and
needs no host code change.

---

## 4. `tsconfig.json` (`src/Gert.Api/wwwroot/tsconfig.json`)

Checker-only; esbuild owns all emit. Key options:

```jsonc
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ESNext",
    "moduleResolution": "Bundler",       // resolves "/lib/van.js" + "./x.js"; right for a no-Node browser target
    "lib": ["ES2022", "DOM", "DOM.Iterable"],
    "types": [],                          // no @types; van via its sidecar .d.ts

    "strict": true,
    "noUnusedLocals": true,
    "noUnusedParameters": true,
    "noImplicitReturns": true,
    "noFallthroughCasesInSwitch": true,
    "noUncheckedIndexedAccess": true,
    "exactOptionalPropertyTypes": true,

    "allowImportingTsExtensions": false,  // imports keep ".js" specifiers, not ".ts"
    "verbatimModuleSyntax": true,         // forces `import type` so esbuild's per-file type elision is correct
    "isolatedModules": true,              // esbuild transpiles per-file; tsgo enforces what that assumes

    "noEmit": true,                       // tsgo is a CHECKER ONLY

    "baseUrl": ".",
    "paths": { "/*": ["./*"] }            // map absolute same-origin specifiers under wwwroot
  },
  "include": ["**/*.ts"]
}
```

**Reconciliation with esbuild.** Dev transpile ignores tsconfig (no bundle ⇒ no `paths` read;
imports already `.js`). The release bundle keeps the existing **throwaway** tsconfig
(`baseUrl` + `paths` only) that `Bundler.cs` already writes — do **not** point esbuild at the strict
tsconfig (esbuild warns on options it doesn't understand). The strict tsconfig is for `tsgo` alone;
comment this split where both are touched.

---

## 5. Vendoring `tsgo` (mirror the esbuild provisioner)

New `tools/Gert.Web.Bundle/TsgoManifest.cs` + `TsgoBinary.cs`, parallel to the esbuild pair (lift
`Download` / `VerifySha512` / `MarkExecutable`). The extraction differs because of the sibling-`lib/`
gotcha.

- **`TsgoManifest`:** `Version = "7.0.0-dev.YYYYMMDD.N"`; per-RID npm key
  `@typescript/native-preview-{rid}` (`linux-x64`, `linux-arm64`, `darwin-x64`, `darwin-arm64`,
  `win32-x64`); tarball URL `https://registry.npmjs.org/@typescript/{key}/-/{key}-{Version}.tgz`;
  SHA-512 = npm `dist.integrity` base64 (strip the `sha512-` prefix) per RID; reuse
  `EsbuildManifest.CurrentRid()`.
- **`TsgoBinary.Ensure()`:** download → `VerifySha512` (FixedTimeEquals, fail-closed) → **extract the
  whole `package/` subtree** (strip the leading `package/`) into a temp dir, preserving the
  binary + `lib/` layout → set exec bit → atomic `Directory.Move` to
  `{temp}/gert-tsgo/{ver}/{rid}/` → write a `.extracted` sentinel that gates the cache hit (proves
  the multi-file extraction completed). **Invoke the binary in place** — never relocate it away from
  `lib/`.
- ⚠ **Confirm tarball layout before coding entry paths:** `curl` one tarball, `tar tzf` it to find
  the binary's exact path and the `lib/` sibling location, and deliberately run the relocated binary
  once to confirm the panic. Then fill `BinaryEntry`/`ExecutableName` and all 5 SHA-512 pins.
- **Invocation:** `--typecheck` mode → `Ensure()` then
  `tsgo --project <wwwroot>/tsconfig.json --noEmit`; stream stderr; exit non-zero on any diagnostic.

### Bumping `tsgo`

Because it's a daily-dev preview, **pin it exactly** for reproducibility. To bump: pick a new
`7.0.0-dev.YYYYMMDD.N`, fetch each RID's `dist.integrity` from
`https://registry.npmjs.org/@typescript/native-preview-<rid>`, refresh all 5 SHA-512 pins, and run
`make typecheck` + the full suite.

---

## 6. Release bundler rework (minimal)

Changes to `tools/Gert.Web.Bundle`:

- **`Program.cs`** becomes a small dispatcher: bare `<wwwroot>` = bundle (the Publish target stays
  unchanged); add `--typecheck`, `--transpile`, `--watch`.
- **`Bundler.cs` `BundleJs`:** entry `app.js` → **`app.ts`** (esbuild bundles `.ts` natively). The
  output stays `app.js`, so `index.html` and `RewriteIndexHtml` are untouched. CSS path unchanged.
- **Fail-closed typecheck pre-step:** `Bundler.Run` calls `tsgo --noEmit` before `BundleJs`; on any
  diagnostic it returns false (publish breaks, raw tree untouched — the existing fail-closed
  contract). This gives the `BundleWebAssets` MSBuild target the gate for free; no `.csproj` change.
- **Prune:** add `".ts"` to the post-bundle prune extension set so no source (incl. `van.d.ts`)
  ships.

---

## 7. Migration order (leaf → root; each stage green & committed)

Always-`.js` import specifiers + sibling emit mean a half-migrated tree always resolves (a `.ts`
importing a not-yet-migrated module still imports `/x.js`, which exists). That is what makes each
stage independently green.

0. **Toolchain only (no renames):** tsconfig, `lib/van.d.ts`, `lib/van-x.d.ts`,
   `TsgoManifest`/`TsgoBinary` + Program dispatcher + modes (`--typecheck`/`--transpile`), the
   `_prepare_transpiled_webroot` build + `ASPNETCORE_WEBROOT` boot in `run.py`, `make
   typecheck`/`transpile`, the CI job, and `TsgoManifestTests`. Verify `make typecheck` is trivially
   0 (no `.ts` yet) and the existing e2e suite still passes when booted against the built mirror.
1. **Leaf libs:** `lib/{format,i18n,action,router,component,artifact-sandbox,markdown-links,
   highlight,smath}.ts` (smath is 38 KB — budget time), `lib/render/{url,inline,lines}.ts`.
2. **Renderer chokepoint:** `lib/render/dom.ts` + `lib/markdown.ts`. **F4 is annotation-only** — no
   logic change; keep `Object.freeze(ALLOW)`/`Object.freeze(NODE_TYPES)` (byte-oracle tests assert
   runtime identity). md-math/md-code stay `.js` until Stage 5 (dom imports them as
   `/components/.../*.js`, which works). Verify with the markdown gallery + byte-oracle tests.
3. **State stores:** `state/{ui,auth,models,chat,knowledge,artifacts}.ts`.
4. **Services:** `services/http.ts` first (everything imports it), then the domain services +
   `icons/icons.ts`.
5. **Components (leaf → composite):** `components/ui/*`, `main/*`, `sidebar/*`, `canvas/**` (incl.
   md-math/md-code), `settings/*`, `app-shell.ts`, `search-overlay.ts`.
6. **Pages + entry:** `pages/admin/users.ts`, `pages/chat.ts`, `app.ts`. `index.html` keeps
   referencing `/app.js`.

**Vendored van** stays `.js`; type via the sidecar `.d.ts` (copy vanjs-core's `van.d.ts` verbatim;
vendor or hand-write `van-x.d.ts` for the tiny used surface — `reactive`, `list`, `calc`,
`stateFields`, `raw`). Don't chase coverage on van's loose `(e: any)` handlers / any-string
`van.tags` (typos stay uncaught) — annotate handlers opportunistically (`(e: Event)` + boundary
narrowing); the goal is the green gate.

---

## 8. CI / Makefile / docs wiring

- **Makefile — every host-boot target transpiles by default:**
  - `make run` (today `dotnet run --project src/Gert.Api`) and `make dev` (the FakeE2E profile) build
    the mirror, then boot with `ASPNETCORE_WEBROOT=<built>` (esbuild `--watch` re-transpiles on save).
    Both are plain `dotnet run` today, so each gains the build + webroot step explicitly.
  - `make serve-mock` transpiles by default (its non-`MINIFY` path); `MINIFY=1` bundles instead.
  - `transpile` (one-shot build via `--transpile`) and `typecheck` (`--typecheck`) targets invoke the
    `tools/Gert.Web.Bundle` tool.
- **Smoke runner (`tools/smoke/run.py`):** the default boot path builds the transpiled mirror
  (`copytree` + `--transpile`, mirroring `_prepare_bundled_webroot`) and passes it as
  `_boot_host(web_root=...)`; `--minify` selects the bundle path instead. Tests are untouched — URLs
  resolve against the mirror. `serve-mock`, `e2e`, and `api-auth` all flow through this.
- **CI (`.github/workflows/ci.yml`):** a new fail-closed **`typecheck`** job (`setup-dotnet` →
  `make typecheck`). The **`e2e`**/**`api-auth`** jobs boot against the built mirror via the runner
  above (no per-job change beyond that the runner now builds first).
- **Tests:** add `tests/Gert.Web.Bundle.Tests/TsgoManifestTests.cs` mirroring `EsbuildManifestTests`
  (version-shape regex incl. the dev suffix, per-RID key map, 64-byte SHA-512 decode, `Current()`
  resolves).
- **Docs (same change, then `make check-links`):** update
  [ui-components.md section 1 + section 6](ui-components.md#6-devrelease-pipeline-no-npm) (source is
  `.ts`; dev build = esbuild transpile-watch + sibling emit + inline maps; release gains the tsgo
  gate; add a "Type checking (tsgo, no npm)" subsection), [spa-style-guide.md](spa-style-guide.md)
  (TS conventions: keep `.js` specifiers, `import type`, van via sidecar `.d.ts`, the factory
  signature in TS), and [tech-stack.md](tech-stack.md) (SPA = VanJS + TypeScript via the native Go
  toolchain; dev build is "esbuild transpile watch", no longer "None"). Demote this doc to a record.

---

## 9. Verification & risks

**Per-stage gate (run all before committing each stage):**

1. `make transpile` — emit succeeds (no esbuild error).
2. `make typecheck` — `tsgo` = 0 diagnostics.
3. `make build` — .NET warnings-as-errors still green.
4. `make test` — incl. `Gert.Web.Bundle.Tests` (+ `TsgoManifestTests`).
5. `make e2e` (or the component subset touching migrated files).
6. **Stages 2 & 6:** `make serve-mock MINIFY=1` to exercise the bundled `app.ts` → `app.js` path.

| Risk | Mitigation |
|---|---|
| Served mirror goes stale vs. source | The mirror is rebuilt on every boot (`copytree` + `--transpile`); `--watch` re-transpiles `.ts` on save. Non-`.js` asset edits need a rebuild — documented dev cost. |
| Clean checkout 404s `.js` (no built mirror yet) | The runner / `make dev` build the mirror before booting; `make transpile` primes it standalone. `wwwroot` itself stays source-only and fully tracked. |
| `tsgo` relocated-binary panic | Extract whole subtree + invoke in place + `.extracted` sentinel; confirm layout via `tar tzf` + a deliberate move-and-panic check. |
| `tsgo` daily-dev churn | Pin exact `7.0.0-dev.…` + 5 SHA-512s; documented bump procedure ([section 5](#bumping-tsgo)). |
| Dev (per-file) vs release (bundle) divergence | Same esbuild binary/version; `serve-mock MINIFY=1` each risky stage. |
| F4 regression in `dom.ts` | Annotation-only edits; keep `Object.freeze`; gallery + byte-oracle tests at Stage 2. |
| `verbatimModuleSyntax` value/type-import mismatch | `isolatedModules` + tsgo catch it; `import type` is mandatory for type-only imports. |
