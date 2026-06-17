# Markdown renderer: reference + fuzzing harness

This folder is two things:

1. A **reference** for Gert's in-house markdown renderer (`src/Gert.Api/wwwroot/lib/markdown.js`
   and its `render/*` + `smath` + `highlight` leaves): each lexer described as a
   well-defined state machine, the *per-character vs per-line* and *regex vs
   regex-free* design choices, and the bounds that keep it total and safe.
2. A **fuzzing harness** that runs the *real, unmodified* renderer headlessly in
   Node and asserts its contracts (total / secure / bounded / well-formed /
   deterministic) over generated + mutated input.

Node is used **only here, and only for fuzzing this part**. The renderer itself
stays no-npm, no-build ESM. The harness has **zero runtime dependencies**: the DOM
is a hand-rolled shim and the only Node built-ins used are `worker_threads`,
`module`, `fs`, `path`, `url`.

```
quick start (from tools/markdown/)
  node selftest.mjs      # prove the harness catches seeded bugs (5 classes) + real renderer is clean
  node check.mjs         # render every corpus/*.md and assert all contracts (fast regression gate)
  node fuzz.mjs --time 20    # broad random fuzz: totality/security/bounds/well-formed/determinism
  node fuzz.mjs --seed 42 -n 50000   # reproducible run by seed
  node complexity.mjs    # systematic super-linear (ReDoS / amplification) growth probe
```

All four tools are green: `selftest` and `check` pass, `fuzz` finds no contract
violations, and `complexity` reports every pattern linear/bounded. Numbers in
section 5.

---

## 1. The system today

`renderMarkdown(src)` is **synchronous, null-safe, and total**: any input yields a
complete `DocumentFragment`, never a throw (one outer `try/catch` degrades to the
literal source). The pipeline:

```
                         render/lines.js                 render/dom.js
  src ──normalize──▶ parseBlocks ──▶ block AST ──▶ render ──▶ DOM ──▶ assignHeadingIds ──▶ fragment
   (CRLF->LF,            │  ▲                          │  │
    tab->4sp)            │  └── parseInline ───────────┘  ├─▶ MdCode ─▶ highlight.js   (fenced code)
                        │      (render/inline.js)        └─▶ MdMath ─▶ smath.js        (math)
                        │                                              ▲
                        └── classifyLine over LINE_KINDS               └ url.js (sanitizeUrl chokepoint, F4)
```

There are **four distinct lexers**, and they make *different* design choices.
That difference is the heart of this reference:

| lexer | file | granularity | regex? | output | bounds |
|---|---|---|---|---|---|
| **block** | `render/lines.js` | **per-line** | **yes** (the classifier) | block AST | `MAX_NEST=32` container depth |
| **inline** | `render/inline.js` | **per-character** | almost none | inline AST | `MAX_INLINE=32` (at render), capped scans |
| **math** | `smath.js` | **per-character** | none (bar `/[a-zA-Z]/`) | MathML AST | `MAX_DEPTH=32`, `MAX_NODES=6000`, `MAX_TEX=8192` |
| **highlight** | `highlight.js` | **per-character** position scan | **yes** (the rules ARE the machine) | `tok-*` spans | `[\s\S]{0,4096}?` cap on every multi-line rule |

So three of the four are already per-character, and two of the four are already
essentially regex-free. The renderer is **not** uniformly one style - and that is
the central point of the "per-char vs per-line / regex vs no-regex" question
(section 3).

---

## 2. Each lexer as a state machine

### 2.1 Block lexer (`render/lines.js`) - per-line, table-driven

The block layer is a **line-oriented pushdown machine**. Its "alphabet" is a
*line kind*, not a character. `classifyLine(line, lookahead, depth)` walks ONE
frozen, ordered table (`LINE_KINDS`) and returns the first matching kind; that
single verdict drives both the dispatch and the paragraph-interrupt, so the two
can never disagree.

```
LINE_KINDS precedence (frozen; asserted at module load):
  FENCE > INDENT_CODE > MATH_DOLLAR > MATH_BRACKET > ATX > THEMATIC
        > BLOCKQUOTE > TABLE_HEADER > LIST_ITEM > SETEXT > (else) PARAGRAPH
```

`parseBlocks` is the driver. As a state machine its states are the block kinds
it is currently accumulating:

```
                  ┌─────────────────────────────────────────────┐
                  │                  TOP (between blocks)         │
                  └─────────────────────────────────────────────┘
   classifyLine(line) = ...
     FENCE        -> IN_FENCE     : copy lines verbatim until isFenceClose -> code_block
     INDENT_CODE  -> IN_INDENT    : copy 4-space-stripped lines (blank-run aware) -> code_block
     MATH_DOLLAR  -> IN_MATH$$    : copy until a `$$` line -> math_block  (one-liner: stay in TOP)
     MATH_BRACKET -> IN_MATH\[    : copy until `\]`        -> math_block  (unclosed: fall back to paragraph)
     ATX/THEMATIC -> emit one block, stay TOP
     BLOCKQUOTE   -> strip "> " prefixes into a sub-document, RECURSE parseBlocks(depth+1)
     TABLE_HEADER -> consume header + delimiter + body rows -> table
     LIST_ITEM    -> IN_LIST      : group items by marker/indent; each item RECURSES parseBlocks(depth+1)
     SETEXT       -> emit heading from the prior line, skip the underline
     PARAGRAPH    -> IN_PARA      : accumulate while classifyLine stays PARAGRAPH/SETEXT
```

- **Pushdown**: blockquote and list nesting are real recursion (`parseBlocks(...,
  depth+1)`). The stack is bounded because `classifyLine` refuses to classify a
  container once `depth >= MAX_NEST` (32) - a would-be `>`/`-` at depth 32 is a
  paragraph line, so `>>>...`x100000 degrades to text instead of recursing.
- **Inline is injected** (`ctx.parseInline`); this module owns no character-level
  logic.
- **Regex**: the classifier is built from ~12 small regexes (`matchFenceOpen`,
  `ATX_RX`, `isDelimiterRow`, `matchListMarker`, ...). They are all **linear** (no
  nested quantifiers / backtracking traps) - section 3 covers the regex stance.

### 2.2 Inline lexer (`render/inline.js`) - per-character, two passes

A single left-to-right character scan (`tokenizeInline`) produces a flat token
list, then `finalizeEmphasis` resolves emphasis/strike via a delimiter stack over
a doubly-linked list. This is the textbook CommonMark inline algorithm.

```
tokenizeInline: while i < N, dispatch on text[i]:
  '\'  -> ESCAPE     : "\n"->hardbreak; "\("->scan <=MATH_INLINE_MAX for "\)"->math; "\<punct>"->literal; else literal '\'
  '`'  -> CODE        : count n backticks, scan for a run of exactly n -> code span (else literal backticks)
  '$'  -> MATH/MONEY  : "$$"=display else inline; scan <=cap for closer; reject if space-adjacent or digit-after ($5 stays money)
  '!['/'[' -> OPENBR  : push a bracket marker on the bracket stack
  ']'  -> CLOSEBR     : pop; if "(" follows, parseLinkTail -> link/image; else the "[" was literal text
  '~~' -> DELIM(~,2)  : strike delimiter (canOpen/canClose by flanking whitespace)
  '*'/'_' -> DELIM    : run of n; canOpen/canClose from left/right-flanking + intraword "_" rule
  '<'  -> AUTOLINK    : <scheme:...> or <a@b> (never HTML)
  '\n' -> BREAK       : trailing "  " -> hardbreak, else softbreak
  h/w/H/W or local-part char at a word boundary -> bare URL / www / email autolink (GFM)
  else -> accumulate into the text buffer
finalizeEmphasis: delimiter-stack pairing -> emph / strong / del nodes
```

- **Two stacks**: a bracket stack (links/images) and the emphasis delimiter
  stack. Both are bounded by the input length; the renderer caps the *rendered*
  emphasis nesting at `MAX_INLINE=32` (deeper degrades to flattened text), so a
  wall of balanced `*` cannot blow the call stack.
- **Bounded scans**: every "look for the closer" scan (`MATH_INLINE_MAX=1024`,
  `MAX_DEST=1024`, `MAX_TITLE=512`) is O(cap) per opener, so a wall of unmatched
  `](` or `$` is O(n), not O(n^2).
- **Regex**: only for autolink shapes (`AUTOLINK_ANGLE`, `URL_RX`, `EMAIL_RX`) and
  the entity decoder - all linear.

### 2.3 Math lexer (`smath.js`) - per-character, regex-free, recursive descent

The cleanest of the four, and the model the others are measured against. A linear
character `lex()` produces TeX tokens; a **bounded recursive-descent** parser
turns them into a MathML descriptor tree; `toDom` materializes it against a closed
element allow-list.

```
lex(src): single pass, NO regex except the /[a-zA-Z]/ control-word char class:
  '%'...\n -> comment    whitespace-run -> one {space}    '^' '_' '{' '}' '&' -> structural
  '\\' + letters -> {cmd, name}   '\\\\' -> {rowbreak}   '\\' + symbol -> control symbol
  other -> {char}

parser (states = grammar productions; depth/count accounted):
  parseRun ──atom*──▶ atoms        (stops at } , \end , \right ; handles \over/\atop/\choose infix)
  parseAtom ─▶ nucleus             ('{'->group, char->charAtom, cmd->cmdAtom)
  attachScripts ─▶ nucleus + ^/_ scripts + primes  (msup/msub/msubsup, or under/over for movablelimits)
  cmdAtom ─▶ dispatch ~200 control words (frac, sqrt, accents, \left..\right, \begin{env}, symbol tables)
```

- **Bounds (this is the well-defined part)**: `P.depth` is incremented around
  every recursive descent and capped at `MAX_DEPTH=32`; `P.count` (via `bump`) is
  capped at `MAX_NODES=6000`; the whole input is capped at `MAX_TEX=8192`. Hit any
  cap -> `P.over` -> degrade to `mtext(src)` (literal). Per-formula `try/catch` in
  `renderMath` keeps a bad formula from taking down the document.
- **Closed output**: `MML_ELEMENTS` (17 tags) + inert presentation attributes
  only. No `href`/`src` sink exists in math, so a formula cannot navigate, fetch,
  or script (`\href{javascript:...}` resolves to nothing).
- **Headless-testable**: `buildMathAst` and `toMathMLString` are DOM-free.

> Sub/superscript chains (`$$___...$$`) build one `<mrow>` of nesting per `_`/`^`
> operator. This *iterative* chain is bounded by `MAX_NODES`/`MAX_TEX` (emitted
> node count and input length), not the `MAX_DEPTH=32` *recursion* cap - so the
> MathML tree can be much deeper than 32 while staying total and bounded.
> `corpus/09-smath-depth.md` pins this deep-but-bounded case.

### 2.4 Highlight lexer (`highlight.js`) - per-character scan, regex-driven rules

A position scanner where, at each index, the language's ordered list of **sticky
(`/y`) regex rules** is tried; the first match emits a `tok-*` span and advances.
Here the regex rules literally *are* the per-language state machine.

```
highlight(code, lang):
  resolve language (alias table) or detectLang(); else -> one plain text node
  i = 0
  while i < len:
    for [rule, class] in RULES[lang]:        # order matters: strings/comments before keywords
      rule.lastIndex = i
      if rule matches at i: flush plain; emit <span class=tok-class>; i += len; break
    else:                                     # no rule: consume one identifier (or one char) as plain
      i += identifier-run or 1
```

- **Per-language sub-machines**: `json`, `python`, `javascript`, `csharp`, `cpp`,
  `rust`, `go`, `bash`, `xml`, `markdown`, each an ordered rule list. Aliases map
  `ts/js/py/c#/...` onto these.
- **Totality**: `highlight` cannot throw on content; an unknown language degrades
  to one text node; a wrong guess only mis-tints (never changes text).
- **The only lexer with regex risk** - some rules use backreferences and lazy
  spans (C++ `R"x(...)x"`, Rust `r#"..."#`, C# verbatim `@"(?:[^"]|"")*"`). Every
  multi-line span is hard-capped (`[\s\S]{0,4096}?`, never an unbounded `[\s\S]*?`),
  so a never-closing opener stays linear instead of rescanning to EOF on each miss.
  This is the renderer's primary ReDoS surface and is fuzzed hard (section 4).

---

## 3. Per-character vs per-line, and regex vs regex-free

The question "should it be a per-character state machine, not per-line, and not
using regex?" has a precise answer once you see that the renderer already mixes
both styles on purpose.

**Where per-character + regex-free already wins (inline, math):** these lexers
deal with *intra-line* structure where a character is the natural unit and where
regex would invite backtracking. They are linear, bounded, and easy to reason
about. `smath` in particular is the gold standard: a regex-free `lex()` + a
depth/node-accounted recursive descent over a closed output set. **Keep them.**

**Where per-line is genuinely the right unit (block):** Markdown's *block* grammar
is defined by CommonMark in terms of lines (a fence opens/closes on a line; a
setext underline is a line; a list item's continuation is decided by leading
indentation columns; paragraphs end at blank lines). The block layer's job is to
**segment lines into containers**, and a line is the honest unit for that. Moving
the block layer to a raw per-character DFA would mean re-deriving "what line am I
on and how is it indented" inside the automaton - more state, not less, and it
would fight the spec rather than mirror it. The current design already isolates
*all* character-level work into the injected inline lexer, so the block layer is a
clean line-pushdown and nothing else.

> **Verdict (per-char vs per-line):** keep the block lexer **per-line**; it is the
> correct granularity for a line-defined grammar, and the per-character work is
> already cleanly delegated to the inline/math lexers. "Make everything
> per-character" would *reduce* well-definedness here, not increase it.

**Regex:** regex is load-bearing and *safe* in three of the four lexers (the block
classifier and the inline/entity patterns are all linear - no nested quantifiers,
no backtracking traps). The one lexer where regex is both gratuitous-in-risk and
hard to bound is **highlight**, whose backreference/lazy-span rules are the only
ReDoS surface in the system.

> **Verdict (regex):** the linear regexes in block/inline stay - rewriting them as
> hand char-scanners would add code without changing behavior or safety.
> `highlight.js` is the one ReDoS-prone lexer, and its multi-line rules are bounded
> to `[\s\S]{0,4096}?` so even a pathological backreference/lazy-span rule is O(cap)
> per opener. The fuzzer's `--slow-ms` gate and `complexity.mjs` are the standing
> regression guards.

**Runnable evidence:** `proto/block-scan.mjs` is a per-character, **zero-regex**
block scanner producing the same AST node shapes as production; `node
proto/compare.mjs` renders it and the production parser through the *same* renderer
and diffs them. Result: **byte-identical on all 13 line-uniform constructs**
(heading / paragraph / fenced code / thematic break / blockquote) -- so per-char +
regex-free block parsing is genuinely feasible -- while lists / tables / setext /
indented code (deliberately omitted) diverge, because those boundaries are defined
by line lookahead + indentation columns. That divergence *is* the answer: a
per-character machine there spends its code re-deriving "what line am I on and how
indented," getting bigger exactly where the per-line classifier is small.

**Unification:** the four lexers *can* be described by one shared shape - "scan an
input stream left to right; at each step consult an ordered set of recognizers;
maintain a bounded stack for nesting" - and this reference documents them that way.
But forcing them to *share code* (one generic transition-table engine) is a poor
trade: the alphabets differ (line-kind vs character vs TeX-token vs regex-rule),
the inline lexer does *backward* delimiter-stack repair (a doubly-linked-list
rewrite a forward DFA can't express), and the block layer is a pushdown over lines
with lookahead. One engine would smuggle all that complexity back in behind a
leakier abstraction - a net loss for a security-first renderer.

> **Verdict (unification):** share the *vocabulary*, not the engine. Two invariants
> run through all four lexers and are exactly what the harness checks: **(A) bounded
> allocation/depth** - `MAX_NEST`/`MAX_INLINE`/`MAX_DEPTH`/`MAX_NODES`/`MAX_TEX`/
> `MATH_INLINE_MAX`/`MAX_DEST`/`MAX_TITLE`/`MAX_TABLE_COLS`/`MAX_TABLE_CELLS`, spelled
> across five files but enforcing one property (the BOUNDED contract); and **(B)
> guaranteed forward progress** - every scan iteration advances the cursor or breaks.
> Keep the two invariants common and the four grammar-shaped bodies distinct.

---

## 4. The fuzzing harness

### What it asserts (the oracle, `lib/oracle.mjs`)

Every input is checked against the renderer's stated contracts:

- **total** - `renderMarkdown` never throws and returns a `DocumentFragment`.
- **secure (F4)** - only allow-list HTML + MathML elements; per-tag attribute
  allow-list; no `on*` handler; no `style` attribute; `href` carries no dangerous
  scheme (RFC-3986 scheme extracted the way a browser would - a colon mid-string
  with no valid scheme is a *relative* URL, not a bypass); `img` `src` is only `#`
  or `data:image/<safe>`; no `<a>`/`<img>` forged inside math.
- **bounded** - output node count <= 60x input; tree depth <= max(512, input
  length) (depth amplification, i.e. *tiny input -> huge tree*, is the bug; depth
  ~= input length is expected and fine).
- **well-formed** - acyclic tree, consistent parent links.
- **deterministic** - two renders of the same input are byte-identical (catches
  leaked module state: regex `lastIndex`, the component `injected` set, etc.).

### How it runs the real renderer in Node (zero deps)

- `lib/dom-shim.mjs` - a ~250-line DOM (element/text/fragment, `createElement[NS]`,
  `appendChild` with fragment-flattening, `classList`/`dataset`/`style`,
  `textContent`, a small `querySelector` subset, `CSSStyleSheet`/`adoptedStyleSheets`).
- `lib/loader.mjs` - a Node ESM resolve hook mapping the renderer's two absolute
  imports (`/components/.../md-math.js`, `md-code.js`) to `wwwroot`, so the
  **exact browser bytes** load unmodified (real `smath`, real `highlight`).
- `lib/render.mjs` - installs the shim, registers the hook, dynamically imports
  `/lib/markdown.js`, and re-exports `renderMarkdown` + pure sub-modules.

### Generation, mutation, isolation, minimization

- `lib/rng.mjs` - seeded mulberry32; every input is a pure function of `(seed,
  index)`, so a failure is reproducible from its seed alone.
- `lib/generators.mjs` - a **grammar-aware** generator (every block + inline
  construct, with an `evil` channel: delimiter walls, near/over-cap nesting,
  unbalanced fences/math/brackets, currency-vs-math, and ReDoS-bait code bodies).
- `lib/mutators.mjs` - byte/structural mutation of seeds (insert interesting
  codepoints: NUL, BOM, zero-width, bidi, surrogate-shaped, astral; duplicate
  spans into walls; truncate to half-open everything; crossover/splice).
- `lib/worker.mjs` - renders each input in a **worker thread** so the parent can
  impose a hard per-input timeout; a true infinite loop or pathological
  super-linear input hangs only the worker, which the parent terminates and
  respawns -> a definitive `hang` verdict.
- `lib/minimize.mjs` - delta-debugging shrinker (chunk/line/char removal + run
  collapse) so a 4 KB failing blob becomes a few-character repro.
- `complexity.mjs` - the **growth-rate** probe: renders a battery of patterns
  across geometric input sizes and fits the time exponent (linear ~1, quadratic
  ~2) + node-count amplification. This is what catches algorithmic-complexity
  bugs (ReDoS, O(n^2) amplification) that a per-input threshold cannot - a
  quadratic that is 2 ms at 8 KB is invisible until it is 40 s at 1 MB.

### Self-test: proving the fuzzer can fail

`node selftest.mjs` injects five deliberately-broken renderers (via a
`GERT_RENDER_MODULE` override the worker honors) and asserts the harness reports
each: a **throw**, an infinite-loop **hang**, an F4 **security** bypass, a
**nondeterminism** bug, and an **unbounded** blow-up - then confirms the real
renderer passes a known-good battery. A fuzzer that never fails is worthless; this
proves ours catches all five failure classes.

### Manual corpus + regression gate

`corpus/*.md` are curated adversarial + feature documents; `node check.mjs`
renders each through the real renderer and asserts every contract (the security
file additionally pins specific neutralizations). It doubles as a fast CI-style
regression gate and as mutation seeds for `fuzz.mjs`.

---

## 5. Results

Validated end-to-end on Node 24:

- **`selftest.mjs`: 14/14** - all five seeded failure classes (throw, hang, F4
  security bypass, nondeterminism, unbounded blow-up) are caught, and the real
  renderer is clean on the known-good battery.
- **`check.mjs`: 9/9 corpus files** satisfy every contract.
- **`fuzz.mjs`: > 1,000,000 inputs** across multiple seeds (general, inline-only,
  corpus-seeded mutation). Average render well under a millisecond; **zero throws,
  zero security / bounds / well-formed / determinism violations, zero hangs.**
- **`complexity.mjs`:** every pattern linear/bounded - the inline `$`/`]` walls,
  emphasis walls, blockquote nesting, tables, and `highlight` code bodies all scale
  linearly, with no node-count amplification.

The renderer's own in-browser gate (`test_markdown_gallery_all_self_checks_pass`)
exercises the same code path in chromium and passes its FEATURE / FUNCTIONAL /
SECURITY self-checks.

The bounds that make this hold are local to each lexer: `MAX_NEST` (block container
depth), `MAX_INLINE` (inline nesting), `MAX_TABLE_COLS` / `MAX_TABLE_CELLS` (table
dimensions), `MATH_INLINE_MAX` / `MAX_DEST` / `MAX_TITLE` (inline scans), `MAX_DEPTH`
/ `MAX_NODES` / `MAX_TEX` (math), and the `[\s\S]{0,4096}?` cap on every multi-line
`highlight` rule. Past any cap the renderer degrades - a truncated table, flattened
nesting, literal text - rather than throwing or allocating without bound.

---

## Files

```
lib/dom-shim.mjs    zero-dep DOM for headless rendering
lib/loader.mjs      ESM hook: "/..." -> wwwroot (loads the real renderer unmodified)
lib/render.mjs      headless entry: renderMarkdown + parseDocument + pure sub-modules
lib/oracle.mjs      the contract checks (total/secure/bounded/well-formed/deterministic)
lib/rng.mjs         seeded PRNG (reproducible)
lib/generators.mjs  grammar-aware random markdown
lib/mutators.mjs    byte/structural mutation of seeds
lib/minimize.mjs    delta-debugging shrinker (async predicate)
lib/worker.mjs      isolated render-runner (per-input timeout -> hang detection)
fuzz.mjs            the broad fuzzer (generate+mutate -> render -> oracle -> minimize -> repro)
complexity.mjs      growth-rate probe (super-linear time + node amplification = ReDoS/O(n^2))
check.mjs           render corpus/*.md and assert all contracts (regression gate)
selftest.mjs        prove the harness catches seeded bugs of all 5 classes
corpus/*.md         curated adversarial + feature documents (also mutation seeds)
proto/block-scan.mjs  EXPLORATION: per-character, zero-regex block scanner
proto/compare.mjs     A/B the prototype vs production block parser (same renderer)
study-workflow.mjs    the multi-agent study script (Claude Code Workflow; provenance, not a node tool)
```
