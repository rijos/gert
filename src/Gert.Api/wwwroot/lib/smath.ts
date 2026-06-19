/*!
 * smath.js — a security-first TeX → native MathML converter.
 *
 * Sibling to smd2/markdown.js: the SAME stance, applied to math. We do NOT ship
 * a third-party TeX engine (KaTeX is CSP-incompatible — it emits inline style=""
 * that `style-src 'self'` strips; Temml is a 477KB parser that runs over model
 * output). Instead we own a small converter and let the BROWSER render the math:
 * native MathML is now in every engine (Firefox forever, Chromium 109+/Jan 2023,
 * WebKit), so a closed set of <math> presentation elements renders with no
 * library at runtime and no inline styles.
 *
 * Why this is safe to reason about:
 *  1. LINEAR lexer. A single left-to-right scan turns the source into a flat
 *     token list — O(n), no backtracking regex, so no ReDoS.
 *  2. BOUNDED recursive descent. Group/script/argument nesting recurses, but the
 *     depth is hard-capped (MAX_DEPTH) and the total emitted node count is capped
 *     (MAX_NODES); past either cap we degrade to literal text. A bounded
 *     configuration space means `{{{{…`×100000 or `\frac\frac…` can neither
 *     recurse to a crash nor allocate without bound.
 *  3. TOTAL over a CLOSED element set (MML_ELEMENTS). Every produced node is a
 *     known MathML presentation element built with createElementNS — never
 *     innerHTML. Unknown control words degrade to a visible <mtext>, they never
 *     mint an element or attribute outside the allow-list. The ONLY attributes we
 *     ever set are inert MathML presentation hints (mathvariant, stretchy, fence,
 *     accent, displaystyle, width, …); there is no href/src sink in math, so a
 *     formula cannot navigate, fetch, or script.
 *  4. NO third-party code touches the input. The converter has zero imports.
 *
 * The parser half (buildMathAst → a plain descriptor tree) is dependency- and
 * DOM-free, so it is unit-testable headless; toDom/renderMath turn that tree into
 * real MathML nodes in the browser.
 */

const MML = 'http://www.w3.org/1998/Math/MathML';

// Render-length cap: a real formula (even a big matrix) fits well under this; the
// ceiling bounds adversarial TeX before the lexer ever runs.
export const MAX_TEX = 8192;
const MAX_DEPTH = 32;     // group/script nesting cap (bounded container stack)
const MAX_NODES = 6000;   // total emitted descriptor cap (bounded allocation)

// The closed allow-list of MathML elements this converter can ever emit. toDom
// throws on anything outside it (fail closed), so the producible DOM is fixed.
const MML_ELEMENTS = new Set([
  'math', 'mrow', 'mi', 'mn', 'mo', 'mtext', 'mspace',
  'msup', 'msub', 'msubsup', 'mover', 'munder', 'munderover',
  'mfrac', 'msqrt', 'mroot', 'mtable', 'mtr', 'mtd',
]);

// The internal MathML descriptor tree (a pure POCO, lowered to DOM by toDom). A node is
// either an element (children) or a text leaf (text); `limits` is a transient marker the
// script-attacher reads and `strip`/serializers drop. Attr values are stringified on emit.
type MAttrs = Record<string, string | number | boolean>;
interface MNode {
  tag: string;
  children?: MNode[] | null;
  text?: string;
  attrs?: MAttrs | null;
  limits?: boolean;
  over?: boolean;
}
const el = (tag: string, children: MNode[], attrs?: MAttrs | null): MNode => ({ tag, children, attrs: attrs || null });
const txt = (tag: string, text: string, attrs?: MAttrs | null): MNode => ({ tag, text, attrs: attrs || null });
const mi = (t: string, a?: MAttrs | null) => txt('mi', t, a);
const mn = (t: string) => txt('mn', t);
const mo = (t: string, a?: MAttrs | null) => txt('mo', t, a);
const mtext = (t: string) => txt('mtext', t);
const mrow = (ch: MNode[]) => el('mrow', ch);
// Implicit grouping: a single atom needs no <mrow>; many atoms get one.
const row = (ch: MNode[]): MNode => (ch.length === 1 ? ch[0]! : el('mrow', ch));

// Greek. Lowercase render italic (variable convention); uppercase upright.
const GREEK: Record<string, string> = {
  alpha: 'α', beta: 'β', gamma: 'γ', delta: 'δ', epsilon: 'ϵ', varepsilon: 'ε',
  zeta: 'ζ', eta: 'η', theta: 'θ', vartheta: 'ϑ', iota: 'ι', kappa: 'κ',
  lambda: 'λ', mu: 'μ', nu: 'ν', xi: 'ξ', omicron: 'ο', pi: 'π', varpi: 'ϖ',
  rho: 'ρ', varrho: 'ϱ', sigma: 'σ', varsigma: 'ς', tau: 'τ', upsilon: 'υ',
  phi: 'ϕ', varphi: 'φ', chi: 'χ', psi: 'ψ', omega: 'ω',
  Gamma: 'Γ', Delta: 'Δ', Theta: 'Θ', Lambda: 'Λ', Xi: 'Ξ', Pi: 'Π',
  Sigma: 'Σ', Upsilon: 'Υ', Phi: 'Φ', Psi: 'Ψ', Omega: 'Ω',
};

// Binary operators / relations / punctuation / misc — all <mo>.
const OPS: Record<string, string> = {
  times: '×', div: '÷', cdot: '⋅', pm: '±', mp: '∓', ast: '∗', star: '⋆',
  circ: '∘', bullet: '∙', oplus: '⊕', ominus: '⊖', otimes: '⊗', oslash: '⊘',
  odot: '⊙', cup: '∪', cap: '∩', sqcup: '⊔', sqcap: '⊓', uplus: '⊎',
  setminus: '∖', smallsetminus: '∖', wedge: '∧', land: '∧', vee: '∨', lor: '∨',
  neg: '¬', lnot: '¬', dagger: '†', ddagger: '‡', amalg: '⨿', wr: '≀',
  leq: '≤', le: '≤', geq: '≥', ge: '≥', neq: '≠', ne: '≠', equiv: '≡',
  approx: '≈', cong: '≅', simeq: '≃', sim: '∼', propto: '∝', asymp: '≍',
  doteq: '≐', ll: '≪', gg: '≫', prec: '≺', succ: '≻', preceq: '⪯', succeq: '⪰',
  subset: '⊂', supset: '⊃', subseteq: '⊆', supseteq: '⊇', sqsubseteq: '⊑',
  sqsupseteq: '⊒', in: '∈', notin: '∉', ni: '∋', owns: '∋',
  forall: '∀', exists: '∃', nexists: '∄', mid: '∣', nmid: '∤', parallel: '∥',
  perp: '⊥', vdash: '⊢', dashv: '⊣', models: '⊨', top: '⊤', bot: '⊥',
  to: '→', rightarrow: '→', gets: '←', leftarrow: '←', leftrightarrow: '↔',
  Rightarrow: '⇒', Leftarrow: '⇐', Leftrightarrow: '⇔', mapsto: '↦',
  longrightarrow: '⟶', longleftarrow: '⟵', longleftrightarrow: '⟷',
  Longrightarrow: '⟹', Longleftarrow: '⟸', hookrightarrow: '↪', hookleftarrow: '↩',
  uparrow: '↑', downarrow: '↓', updownarrow: '↕', nearrow: '↗', searrow: '↘',
  swarrow: '↙', nwarrow: '↖', implies: '⟹', impliedby: '⟸', iff: '⟺',
  triangleq: '≜', triangleleft: '◁', triangleright: '▷', bowtie: '⋈',
  ldots: '…', cdots: '⋯', vdots: '⋮', ddots: '⋱', dots: '…', dotsc: '…',
  colon: ':',
};

// Symbols that read as identifiers / constants — <mi>.
const IDENTS: Record<string, string> = {
  infty: '∞', partial: '∂', nabla: '∇', emptyset: '∅', varnothing: '∅',
  aleph: 'ℵ', beth: 'ℶ', hbar: 'ℏ', hslash: 'ℏ', ell: 'ℓ', wp: '℘',
  Re: 'ℜ', Im: 'ℑ', mho: '℧', Finv: 'Ⅎ', Game: '⅁', imath: 'ı', jmath: 'ȷ',
  angle: '∠', measuredangle: '∡', sphericalangle: '∢', triangle: '△',
  square: '□', blacksquare: '■', lozenge: '◊', bigstar: '★', diamond: '⋄',
  flat: '♭', natural: '♮', sharp: '♯', clubsuit: '♣', diamondsuit: '♦',
  heartsuit: '♥', spadesuit: '♠', surd: '√', backslash: '\\', degree: '°',
  checkmark: '✓', maltese: '✠', P: '¶', S: '§',
};

// Big operators. `limits:true` => sub/sup move under/over in display mode.
const BIGOPS: Record<string, [string, boolean]> = {
  sum: ['∑', true], prod: ['∏', true], coprod: ['∐', true],
  int: ['∫', false], iint: ['∬', false], iiint: ['∭', false], oint: ['∮', false],
  bigcup: ['⋃', true], bigcap: ['⋂', true], bigsqcup: ['⨆', true],
  bigvee: ['⋁', true], bigwedge: ['⋀', true],
  bigoplus: ['⨁', true], bigotimes: ['⨂', true], bigodot: ['⨀', true],
  biguplus: ['⨄', true],
};

// Named operators rendered upright. `true` => limits move under/over in display.
const FUNCS: Record<string, boolean> = {
  sin: false, cos: false, tan: false, cot: false, sec: false, csc: false,
  arcsin: false, arccos: false, arctan: false, sinh: false, cosh: false,
  tanh: false, coth: false, log: false, ln: false, lg: false, exp: false,
  deg: false, arg: false, dim: false, hom: false, ker: false,
  lim: true, limsup: true, liminf: true, max: true, min: true, sup: true,
  inf: true, det: true, gcd: true, Pr: true, mod: false, bmod: false,
};

// Stretchy fence delimiters used by \left … \right (and standalone).
const DELIMS: Record<string, string> = {
  '(': '(', ')': ')', '[': '[', ']': ']', '|': '|',
  'lbrace': '{', '{': '{', 'rbrace': '}', '}': '}',
  'langle': '⟨', 'rangle': '⟩', 'lfloor': '⌊', 'rfloor': '⌋',
  'lceil': '⌈', 'rceil': '⌉', 'vert': '|', 'Vert': '‖', 'lvert': '|',
  'rvert': '|', 'lVert': '‖', 'rVert': '‖', 'backslash': '\\', '/': '/',
  'uparrow': '↑', 'downarrow': '↓', '.': '',
};

// Accent commands -> [accent char, stretchy?].
const ACCENTS: Record<string, [string, boolean]> = {
  hat: ['^', false], widehat: ['^', true], check: ['ˇ', false], breve: ['˘', false],
  acute: ['´', false], grave: ['`', false], tilde: ['~', false], widetilde: ['~', true],
  bar: ['¯', false], vec: ['→', false], dot: ['˙', false], ddot: ['¨', false],
  dddot: ['⃛', false], mathring: ['˚', false], overline: ['‾', true],
  overrightarrow: ['→', true], overleftarrow: ['←', true],
};

// \mathXX font commands -> the MathML mathvariant they set.
const VARIANTS: Record<string, string> = {
  mathbb: 'double-struck', mathbf: 'bold', boldsymbol: 'bold-italic',
  mathcal: 'script', mathscr: 'script', mathfrak: 'fraktur',
  mathsf: 'sans-serif', mathtt: 'monospace', mathrm: 'normal',
  mathit: 'italic', mathnormal: 'italic', textrm: 'normal', textbf: 'bold',
  textit: 'italic', texttt: 'monospace',
};

// Whitespace commands -> mspace width (em).
const SPACES: Record<string, string> = {
  ',': '0.167em', ':': '0.222em', ';': '0.278em', '!': '-0.167em',
  ' ': '0.25em', quad: '1em', qquad: '2em', enspace: '0.5em',
  thinspace: '0.167em', medspace: '0.222em', thickspace: '0.278em',
  negthinspace: '-0.167em', '~': '0.25em',
};

// A lexer token; `v` is set for char/cmd kinds and absent for the structural kinds.
// `v` is widened with an explicit `| undefined` because the lexer builds tokens from
// `src[i]` (a possibly-undefined index read) - exactOptionalPropertyTypes needs the union.
interface Tok { k: string; v?: string | undefined; }
function lex(src: string): Tok[] {
  const toks: Tok[] = [];
  let i = 0;
  const n = src.length;
  while (i < n) {
    const c = src[i];
    if (c === '%') { while (i < n && src[i] !== '\n') i++; continue; }      // TeX comment
    if (c === ' ' || c === '\t' || c === '\n' || c === '\r') {              // collapse a run to ONE
      while (i < n && (src[i] === ' ' || src[i] === '\t' || src[i] === '\n' || src[i] === '\r')) i++;
      toks.push({ k: 'space' });                                            // ignored in math, kept in \text
      continue;
    }
    if (c === '^') { toks.push({ k: '^' }); i++; continue; }
    if (c === '_') { toks.push({ k: '_' }); i++; continue; }
    if (c === '{') { toks.push({ k: '{' }); i++; continue; }
    if (c === '}') { toks.push({ k: '}' }); i++; continue; }
    if (c === '&') { toks.push({ k: '&' }); i++; continue; }
    if (c === '~') { toks.push({ k: 'cmd', v: '~' }); i++; continue; }
    if (c === '\\') {
      const d = src[i + 1];
      if (d === undefined) { toks.push({ k: 'char', v: '\\' }); i++; continue; }
      if (d === '\\') { toks.push({ k: 'rowbreak' }); i += 2; continue; }     // \\ row break
      if (/[a-zA-Z]/.test(d)) {                                              // control word
        let j = i + 1;
        while (j < n && /[a-zA-Z]/.test(src[j]!)) j++;
        let name = src.slice(i + 1, j);
        if (src[j] === '*') { name += '*'; j++; }                            // \operatorname* etc.
        toks.push({ k: 'cmd', v: name });
        i = j;
        while (i < n && (src[i] === ' ' || src[i] === '\t')) i++;            // gobble trailing space
        continue;
      }
      toks.push({ k: 'cmd', v: d });                                         // control symbol \, \{ \$ …
      i += 2; continue;
    }
    toks.push({ k: 'char', v: c });
    i++;
  }
  return toks;
}

// Mutable parser state threaded through the recursive descent (cursor + bounded counters).
interface PState { toks: Tok[]; i: number; depth: number; count: number; over: boolean; }
function parser(toks: Tok[]): PState {
  return { toks, i: 0, depth: 0, count: 0, over: false };
}
const peek = (P: PState): Tok | undefined => P.toks[P.i];
const bump = (P: PState) => { if (++P.count > MAX_NODES) P.over = true; };
const skipSpaces = (P: PState) => { while (P.i < P.toks.length && P.toks[P.i]!.k === 'space') P.i++; };

// Parse a run of atoms (each = nucleus + scripts) until a stopper. `cell` mode
// (inside an environment) stops at & / \\ / \end; otherwise those are consumed
// and ignored so the pass stays total. Always stops at } and EOF.
function parseRun(P: PState, cell: boolean): MNode[] {
  const atoms: MNode[] = [];
  if (P.depth++ > MAX_DEPTH || P.over) { P.depth--; if (!P.over) atoms.push(mtext('…')); return atoms; }
  while (P.i < P.toks.length && !P.over) {
    // P.i < P.toks.length holds, so peek(P) is a defined token here.
    const tk = peek(P)!;
    if (tk.k === 'space') { P.i++; continue; }
    if (tk.k === '}') break;
    if (tk.k === 'cmd' && (tk.v === 'end' || tk.v === 'right')) break;      // \right ends a \left group
    if (tk.k === '&' || tk.k === 'rowbreak') { if (cell) break; P.i++; continue; }
    // infix fraction operators: everything before \over/\atop/\choose is the
    // numerator, the rest of this run is the denominator.
    if (tk.k === 'cmd' && (tk.v === 'over' || tk.v === 'atop' || tk.v === 'choose')) {
      P.i++;
      const num = row(atoms.splice(0, atoms.length));
      const den = row(parseRun(P, cell));
      atoms.push(tk.v === 'choose'
        ? fenced('(', el('mfrac', [num, den], { linethickness: '0' }), ')')
        : el('mfrac', [num, den], tk.v === 'atop' ? { linethickness: '0' } : null));
      break;                                                 // the remainder was consumed by the recursive parseRun
    }
    let base = parseAtom(P);
    if (base == null) continue;
    base = attachScripts(P, base);
    atoms.push(base);
  }
  P.depth--;
  return atoms;
}

// One nucleus (no scripts). Returns a descriptor, or null if nothing consumable.
// `single` (set when serving a brace-free argument) keeps a digit from greedily
// swallowing following tokens, so \frac12 == \frac{1}{2} (TeX single-token arg).
function parseAtom(P: PState, single?: boolean): MNode | null {
  skipSpaces(P);
  const tk = peek(P);
  if (tk == null) return null;
  if (tk.k === '^' || tk.k === '_') return mrow([]);                 // empty base; scripts attach next
  // peek(P) && peek(P).k guards the second call (P.i unchanged), so peek(P)! is the same tok.
  if (tk.k === '{') { P.i++; const inner = parseRun(P, false); if (peek(P) && peek(P)!.k === '}') P.i++; bump(P); return row(inner); }
  // char/cmd tokens always carry `v` (set by the lexer).
  if (tk.k === 'char') { P.i++; return charAtom(P, tk.v!, single); }
  if (tk.k === 'cmd') { P.i++; return cmdAtom(P, tk.v!); }
  P.i++; return null;
}

function charAtom(P: PState, c: string, single?: boolean): MNode {
  bump(P);
  if (c >= '0' && c <= '9') {                                        // group a number into one <mn>
    let s = c;
    if (!single) while (peek(P) && peek(P)!.k === 'char' && /[0-9.]/.test(peek(P)!.v!)) { s += peek(P)!.v!; P.i++; }
    return mn(s);
  }
  if (/[A-Za-z]/.test(c)) return mi(c);                              // one identifier per letter (TeX semantics)
  if (c === '-') return mo('−');                                     // ASCII hyphen -> real minus
  if (c === "'") return mo('′');
  if (c === '(' || c === ')' || c === '[' || c === ']' || c === '|') return mo(c, { stretchy: 'false' });
  if (c === '.' || c === ',' || c === ';' || c === '!' || c === '?') return mo(c);
  return mo(c);
}

// Parse a single argument: a {group}, or the next single atom (TeX semantics).
function parseArg(P: PState): MNode {
  skipSpaces(P);
  const tk = peek(P);
  if (tk == null) return mrow([]);
  if (tk.k === '{') { P.i++; const inner = parseRun(P, false); if (peek(P) && peek(P)!.k === '}') P.i++; bump(P); return row(inner); }
  // Brace-free arg = ONE token. Depth-account this branch too: cmdAtom -> parseArg
  // -> parseAtom -> cmdAtom is the recursion MAX_DEPTH must bound (\frac\frac… /
  // \sqrt\sqrt… / \hat\hat… chains), and it never runs through parseRun.
  if (P.over || P.depth >= MAX_DEPTH) return mtext('…');
  P.depth++;
  const a = parseAtom(P, true);
  P.depth--;
  return a == null ? mrow([]) : a;
}

// Collect atoms until a literal ']' char (used for the \sqrt index option).
function parseBracketArg(P: PState): MNode | null {
  skipSpaces(P);
  if (!peek(P) || peek(P)!.k !== 'char' || peek(P)!.v !== '[') return null;
  P.i++;
  const atoms: MNode[] = [];
  if (P.depth++ > MAX_DEPTH) { P.depth--; return mrow([]); }
  while (P.i < P.toks.length && !P.over) {
    const tk = peek(P)!;
    if (tk.k === 'char' && tk.v === ']') { P.i++; break; }
    if (tk.k === '}') break;
    let b = parseAtom(P);
    if (b == null) continue;
    b = attachScripts(P, b);
    atoms.push(b);
  }
  P.depth--;
  return row(atoms);
}

// super/subscripts + primes following a nucleus.
function attachScripts(P: PState, base: MNode): MNode {
  let sub: MNode | null = null, sup: MNode | null = null;
  while (P.i < P.toks.length && !P.over) {
    skipSpaces(P);
    const tk = peek(P);
    if (tk == null) break;
    if (tk.k === '^') { P.i++; sup = sup ? row([sup, parseArg(P)]) : parseArg(P); }
    else if (tk.k === '_') { P.i++; sub = sub ? row([sub, parseArg(P)]) : parseArg(P); }
    else if (tk.k === 'char' && tk.v === "'") {
      let primes = '';
      while (peek(P) && peek(P)!.k === 'char' && peek(P)!.v === "'") { primes += '′'; P.i++; }
      sup = sup ? row([sup, mo(primes)]) : mo(primes);
    } else break;
  }
  if (sub == null && sup == null) return base;
  bump(P);
  const limits = base && base.limits;                       // movablelimits big-op / function
  if (limits) {
    if (sub && sup) return el('munderover', [strip(base), sub, sup]);
    if (sub) return el('munder', [strip(base), sub]);
    return el('mover', [strip(base), sup!]);
  }
  if (sub && sup) return el('msubsup', [base, sub, sup]);
  if (sub) return el('msub', [base, sub]);
  return el('msup', [base, sup!]);
}
// Drop the transient `limits` marker before a node lands in the tree.
const strip = (d: MNode): MNode => { if (d && d.limits) { const { limits, ...rest } = d; return rest; } return d; };

// \not<rel> -> the precomposed negated relation where Unicode has one.
const NOT_MAP: Record<string, string> = {
  '=': '≠', '<': '≮', '>': '≯', '∈': '∉', '∋': '∌', '≡': '≢', '∼': '≁', '≃': '≄',
  '≅': '≇', '≈': '≉', '≤': '≰', '≥': '≱', '⊂': '⊄', '⊃': '⊅', '⊆': '⊈', '⊇': '⊉',
};

function cmdAtom(P: PState, name: string): MNode {
  bump(P);
  if (name === 'frac' || name === 'dfrac' || name === 'tfrac' || name === 'cfrac') {
    const a = parseArg(P), b = parseArg(P);
    const attrs = name === 'dfrac' ? { displaystyle: 'true' } : name === 'tfrac' ? { displaystyle: 'false' } : null;
    return el('mfrac', [a, b], attrs);
  }
  if (name === 'binom' || name === 'dbinom' || name === 'tbinom') {
    const a = parseArg(P), b = parseArg(P);
    return fenced('(', el('mfrac', [a, b], { linethickness: '0' }), ')');
  }
  if (name === 'sqrt') {
    const idx = parseBracketArg(P);
    const arg = parseArg(P);
    return idx ? el('mroot', [arg, idx]) : el('msqrt', [arg]);
  }
  if (ACCENTS[name]) {
    const [ch, stretchy] = ACCENTS[name];
    const arg = parseArg(P);
    const acc = mo(ch, { stretchy: stretchy ? 'true' : 'false', accent: 'true' });
    return el('mover', [arg, acc], { accent: 'true' });
  }
  if (name === 'overbrace') return el('mover', [parseArg(P), mo('⏞', { stretchy: 'true' })], { accent: 'true' });
  if (name === 'underbrace') return el('munder', [parseArg(P), mo('⏟', { stretchy: 'true' })], { accent: 'true' });
  if (name === 'overset') { const top = parseArg(P), bot = parseArg(P); return el('mover', [bot, top]); }
  if (name === 'underset') { const bot = parseArg(P), top = parseArg(P); return el('munder', [top, bot]); }
  if (name === 'stackrel') { const top = parseArg(P), bot = parseArg(P); return el('mover', [bot, top]); }
  if (name === 'underline') return el('munder', [parseArg(P), mo('_', { stretchy: 'true' })], { accent: 'true' });
  // --- boxed/cancel: MathML Core has no <menclose>, so render the CONTENT and
  // drop the decoration (far better than the literal "\boxed" the fallthrough gives).
  if (name === 'boxed' || name === 'cancel' || name === 'bcancel' || name === 'xcancel') return parseArg(P);
  if (name === 'xrightarrow' || name === 'xleftarrow' || name === 'xRightarrow' || name === 'xLeftarrow') {
    parseBracketArg(P);                                       // ignore the optional [under] label
    const arrow = /left/i.test(name) ? (name[1] === 'L' ? '⇐' : '←') : (name[1] === 'R' ? '⇒' : '→');
    return el('mover', [mo(arrow, { stretchy: 'true' }), parseArg(P)]);
  }
  if (name === 'pmod') return el('mrow', [txt('mspace', '', { width: '0.444em' }), mo('('), mi('mod', { mathvariant: 'normal' }), txt('mspace', '', { width: '0.333em' }), parseArg(P), mo(')')]);
  if (name === 'pod') return el('mrow', [txt('mspace', '', { width: '0.444em' }), mo('('), parseArg(P), mo(')')]);
  if (name === 'text' || name === 'textnormal' || name === 'mbox') return mtext(rawText(P));
  if (name === 'operatorname' || name === 'operatorname*') {
    const s = rawText(P);
    if (name === 'operatorname*') { const node = mo(s, { movablelimits: 'true', lspace: '0', rspace: '0' }); node.limits = true; return node; }
    return mi(s, { mathvariant: 'normal' });
  }
  if (VARIANTS[name]) return applyVariant(parseArg(P), VARIANTS[name]);
  // --- colour: \textcolor{c}{x} / \color{c}{x}. MathML `mathcolor` is a
  //     presentation ATTRIBUTE (not an inline style="", which our style-src CSP
  //     would strip — the very reason KaTeX is out), so the colour survives. The
  //     value is charset-validated in applyColor before it reaches the attribute.
  if (name === 'color' || name === 'textcolor') { const c = rawText(P); return applyColor(parseArg(P), c); }
  // --- chemistry (mhchem): \ce{…}/\pu{…} lower onto the SAME MathML leaf set.
  if (name === 'ce' || name === 'pu') return parseChem(rawText(P));
  if (SPACES[name] != null) return txt('mspace', '', { width: SPACES[name] });
  if (name === 'left') return parseLeftRight(P);
  if (name === 'right') return mrow([]);                   // unmatched \right -> nothing
  if (name === 'bigl' || name === 'bigr' || name === 'Bigl' || name === 'Bigr' ||
      name === 'biggl' || name === 'biggr' || name === 'Biggl' || name === 'Biggr' ||
      name === 'big' || name === 'Big' || name === 'bigg' || name === 'Bigg') {
    return delimAtom(P);                                   // sized delim: take the next delimiter token
  }
  if (name === 'begin') return parseEnv(P);
  if (name === 'end') return mrow([]);
  if (name === '{' || name === '}') return mo(name, { stretchy: 'false' });
  if (name === '$' || name === '%' || name === '#' || name === '&' || name === '_') return mo(name);
  if (name === ' ') return txt('mspace', '', { width: '0.25em' });
  if (name === ',' || name === ':' || name === ';' || name === '!') return txt('mspace', '', { width: SPACES[name]! }); // each of these keys exists in SPACES
  if (name === '|') return mo('‖', { stretchy: 'false' });
  if (name === 'not') {
    const a = parseArg(P);
    const base = a && (a.tag === 'mo' || a.tag === 'mi') ? a.text : null;
    if (base && NOT_MAP[base]) return mo(NOT_MAP[base]);     // precomposed negation (≠, ∉, ⊄, …)
    if (base) return mo(base + '̸');                    // overlay AFTER the base grapheme, in ONE <mo>
    return el('mrow', [a, mo('̸')]);
  }
  if (GREEK[name]) return mi(GREEK[name]);
  if (IDENTS[name]) return mi(IDENTS[name]);
  if (OPS[name]) return mo(OPS[name]);
  if (DELIMS[name] != null && DELIMS[name] !== '') return mo(DELIMS[name], { stretchy: 'false' });
  if (BIGOPS[name]) { const [g, lim] = BIGOPS[name]; const node = mo(g, { largeop: 'true', movablelimits: lim ? 'true' : 'false' }); node.limits = lim; return node; }
  if (FUNCS[name] != null) {
    const base = name.replace(/\*$/, '');
    // Limit-bearing names (lim/max/sup/…) are <mo movablelimits> so the browser
    // puts scripts under/over in display and beside them inline — like big ops.
    if (FUNCS[name]) { const node = mo(base, { movablelimits: 'true', lspace: '0', rspace: '0' }); node.limits = true; return node; }
    return mi(base, { mathvariant: 'normal' });
  }
  // --- fail closed: unknown control word renders as visible, inert literal text
  return mtext('\\' + name);
}

// Read a {group} as plain text (for \text / \operatorname) without math parsing.
function rawText(P: PState): string {
  if (!peek(P) || peek(P)!.k !== '{') {
    const a = parseAtom(P);
    return a && a.text != null ? a.text : '';
  }
  P.i++;
  let s = '';
  let d = 1;
  while (P.i < P.toks.length) {
    const tk = peek(P)!; P.i++;
    if (tk.k === '{') { d++; s += '{'; continue; }
    if (tk.k === '}') { if (--d === 0) break; s += '}'; continue; }
    if (tk.k === 'char') s += tk.v!;        // char tokens always carry `v`
    else if (tk.k === 'space') s += ' ';
    else if (tk.k === 'cmd') s += tk.v === ' ' ? ' ' : '\\' + tk.v;
    else if (tk.k === '^') s += '^';
    else if (tk.k === '_') s += '_';
    else if (tk.k === '&') s += '&';
  }
  return s;
}

// Apply a mathvariant to every identifier/number leaf in a parsed argument.
function applyVariant(node: MNode, variant: string): MNode {
  const visit = (d: MNode): MNode => {
    if (!d) return d;
    // mi/mn are text leaves, so d.text is set; assert it for txt().
    if (d.tag === 'mi' || d.tag === 'mn') return txt(d.tag, d.text!, { ...(d.attrs || {}), mathvariant: variant });
    if (d.children) return { ...d, children: d.children.map(visit) };
    return d;
  };
  return visit(node);
}

// Wrap a node so its descendants inherit a colour, via the inert MathML
// `mathcolor` attribute. The value is validated to a #hex or a plain colour name
// (no punctuation, bounded length) — anything else renders UNCOLOURED rather than
// letting a model-authored string reach the attribute.
const COLOR_OK = /^(#[0-9a-fA-F]{3,8}|[a-zA-Z]{1,24})$/;
function applyColor(node: MNode, color: string): MNode {
  const c = String(color ?? '').trim();
  return COLOR_OK.test(c) ? el('mrow', [node], { mathcolor: c }) : node;
}

// mhchem (\ce / \pu): same stance as the math path. A linear left-to-right scan
// lowers the formula onto the SAME closed MathML leaf set, so it inherits every
// safety property -- no element/attribute outside the math allow-list, no inline
// style, bounded by the shared MAX_NODES/MAX_DEPTH caps. A PRACTICAL subset;
// isotope prescripts and bond ornaments degrade to plain glyphs, never a new element.
const CHEM_ARROWS: [string, string][] = [
  ['<=>>', '⇌'], ['<<=>', '⇌'], ['<=>', '⇌'], ['<->', '↔'], ['->', '→'], ['<-', '←'],
];
function matchArrow(s: string, i: number): { len: number; ch: string } | null {
  for (const [tok, ch] of CHEM_ARROWS) if (s.startsWith(tok, i)) return { len: tok.length, ch };
  return null;
}

function parseChem(src: string): MNode {
  const s = String(src ?? '');
  const n = s.length;
  let i = 0;
  let nodes = 0;
  const state = { over: false };
  const bumpc = () => { if (++nodes > MAX_NODES) state.over = true; };

  // Attach a sub/sup to the most recent atom, combining into <msubsup> as needed.
  // The msup/msub branch reads b.children[0..1], which those tags always have.
  const attach = (out: MNode[], kind: string, sc: MNode) => {
    if (!out.length) out.push(mrow([]));
    const b = out[out.length - 1]!;
    if (kind === 'sub') {
      out[out.length - 1] = b.tag === 'msup'
        ? el('msubsup', [b.children![0]!, sc, b.children![1]!])
        : el('msub', [b, sc]);
    } else {
      out[out.length - 1] = b.tag === 'msub'
        ? el('msubsup', [b.children![0]!, b.children![1]!, sc])
        : el('msup', [b, sc]);
    }
    bumpc();
  };

  // A script body: a {group}, or a run of digits with an optional trailing sign.
  const readScript = (depth: number): MNode => {
    if (s[i] === '{') { i++; const inner = seq('}', depth + 1); if (s[i] === '}') i++; return row(inner); }
    let j = i;
    while (j < n && s[j]! >= '0' && s[j]! <= '9') j++;
    let sign: string | null = null;
    if (s[j] === '+' || s[j] === '-') { sign = s[j]!; j++; }
    const digits = s.slice(i, sign ? j - 1 : j);
    i = j;
    const parts: MNode[] = [];
    if (digits) parts.push(mn(digits));
    if (sign) parts.push(mo(sign === '-' ? '−' : '+'));
    return parts.length ? row(parts) : mrow([]);
  };

  // A backslash command inside chemistry: known symbols only; else inert literal.
  const cmd = (name: string): MNode => {
    if (GREEK[name]) return mi(GREEK[name]);
    if (IDENTS[name]) return mi(IDENTS[name]);
    if (OPS[name]) return mo(OPS[name]);
    if (name === 'cdot') return mo('⋅');
    if (SPACES[name] != null) return txt('mspace', '', { width: SPACES[name] });
    return mtext('\\' + name);
  };

  // $…$ inside \ce escapes to the math grammar (and inherits its bounds).
  const mathEscape = (): MNode => {
    let j = i;
    while (j < n && s[j] !== '$') j++;
    const inner = s.slice(i, j);
    i = j < n ? j + 1 : j;
    return row(parseRun(parser(lex(inner)), false));
  };

  // An arrow glyph with optional [over] and [over][under] labels.
  const arrowNode = (ch: string, depth: number): MNode => {
    const a = mo(ch, { stretchy: 'true' });
    let over = null, under = null;
    if (s[i] === '[') { i++; over = row(seq(']', depth + 1)); if (s[i] === ']') i++; }
    if (s[i] === '[') { i++; under = row(seq(']', depth + 1)); if (s[i] === ']') i++; }
    if (over && under) return el('munderover', [a, under, over]);
    if (over) return el('mover', [a, over]);
    return a;
  };

  // Parse a run until a stop char (or end). `space` tracks whether a separator
  // preceded the cursor, which decides coefficient-vs-subscript and charge-vs-plus.
  function seq(stop: string | null, depth: number): MNode[] {
    const out: MNode[] = [];
    if (depth > MAX_DEPTH) return out;
    let space = true;
    while (i < n && !state.over) {
      // i < n holds, so s[i] is a defined char.
      const c = s[i]!;
      if (stop && stop.indexOf(c) !== -1) break;
      if (c === ' ' || c === '\t' || c === '\n' || c === '\r') { i++; space = true; continue; }
      const ar = matchArrow(s, i);
      if (ar) { i += ar.len; out.push(arrowNode(ar.ch, depth)); space = false; bumpc(); continue; }
      if (/[A-Za-z]/.test(c)) {                                  // element/state letters -> upright
        let j = i; while (j < n && /[A-Za-z]/.test(s[j]!)) j++;
        out.push(mi(s.slice(i, j), { mathvariant: 'normal' })); i = j; space = false; bumpc(); continue;
      }
      if (c >= '0' && c <= '9') {                                // count: subscript after an atom, else coefficient
        let j = i; while (j < n && ((s[j]! >= '0' && s[j]! <= '9') || s[j] === '.')) j++;
        const num = s.slice(i, j); i = j;
        if (!space && out.length) attach(out, 'sub', mn(num));
        else { out.push(mn(num)); bumpc(); }
        space = false; continue;
      }
      if (c === '^' || c === '_') { i++; attach(out, c === '^' ? 'sup' : 'sub', readScript(depth)); space = false; continue; }
      if (c === '+' || c === '-') {                              // charge (after an atom) vs operator/bond
        const after = s[i + 1] || '';
        if (!space && out.length && !/[A-Za-z([]/.test(after)) { i++; attach(out, 'sup', mo(c === '-' ? '−' : '+')); space = false; continue; }
        i++; out.push(mo(c === '-' ? '−' : '+')); space = c === '+'; bumpc(); continue;
      }
      if (c === '=') { i++; out.push(mo('=')); space = false; bumpc(); continue; }   // double bond
      if (c === '#') { i++; out.push(mo('≡')); space = false; bumpc(); continue; }   // triple bond
      if (c === '*') { i++; out.push(mo('⋅')); space = true; bumpc(); continue; }    // addition-compound dot
      if (c === '/') { i++; out.push(mo('/')); space = false; bumpc(); continue; }
      if (c === '(' || c === '[') {                              // delimiter group -> one subscriptable unit
        const close = c === '(' ? ')' : ']'; i++;
        const inner = seq(close, depth + 1); if (s[i] === close) i++;
        out.push(el('mrow', [mo(c, { stretchy: 'false' }), ...inner, mo(close, { stretchy: 'false' })]));
        space = false; bumpc(); continue;
      }
      if (c === '{') { i++; const inner = seq('}', depth + 1); if (s[i] === '}') i++; out.push(row(inner)); space = false; bumpc(); continue; }
      if (c === ')' || c === ']' || c === '}') { i++; continue; }   // stray close -> drop
      if (c === '\\') {
        let j = i + 1; while (j < n && /[a-zA-Z]/.test(s[j]!)) j++;
        const name = j > i + 1 ? s.slice(i + 1, j) : s[i + 1] || '';
        i = j > i + 1 ? j : i + 2;
        out.push(cmd(name)); space = false; bumpc(); continue;
      }
      if (c === '$') { i++; out.push(mathEscape()); space = false; continue; }
      out.push(mo(c)); i++; space = false; bumpc();                // anything else -> inert glyph
    }
    return out;
  }

  return row(seq(null, 0));
}

// Wrap a node in stretchy fences -> mrow( mo(open), node, mo(close) ).
function fenced(open: string, inner: MNode, close: string): MNode {
  const kids: MNode[] = [];
  if (open) kids.push(mo(open, { fence: 'true', stretchy: 'true' }));
  kids.push(inner);
  if (close) kids.push(mo(close, { fence: 'true', stretchy: 'true' }));
  return el('mrow', kids);
}

// Read the delimiter token that follows \left / \right / \big… .
function readDelim(P: PState): string {
  skipSpaces(P);
  const tk = peek(P);
  if (!tk) return '';
  // char/cmd tokens always carry `v`; DELIMS[v] is string|undefined, the != null guards it.
  if (tk.k === 'char') { P.i++; return DELIMS[tk.v!] != null ? DELIMS[tk.v!]! : tk.v!; }
  if (tk.k === 'cmd') { P.i++; return DELIMS[tk.v!] != null ? DELIMS[tk.v!]! : (tk.v === '.' ? '' : ''); }
  return '';
}
function delimAtom(P: PState): MNode {
  const ch = readDelim(P);
  return ch ? mo(ch, { stretchy: 'false' }) : mrow([]);
}

// \left X … \right Y : parse the body, fence it with stretchy delimiters.
function parseLeftRight(P: PState): MNode {
  const open = readDelim(P);
  const body = parseRun(P, false);
  let close = '';
  if (peek(P) && peek(P)!.k === 'cmd' && peek(P)!.v === 'right') { P.i++; close = readDelim(P); }
  return fenced(open, row(body), close);
}

const ENV_FENCE: Record<string, [string, string]> = {
  pmatrix: ['(', ')'], bmatrix: ['[', ']'], Bmatrix: ['{', '}'],
  vmatrix: ['|', '|'], Vmatrix: ['‖', '‖'], matrix: ['', ''],
  smallmatrix: ['', ''], array: ['', ''], cases: ['{', ''],
  aligned: ['', ''], align: ['', ''], 'align*': ['', ''],
  gathered: ['', ''], gather: ['', ''], split: ['', ''],
};
function parseEnv(P: PState): MNode {
  // name: \begin{ NAME }
  let name = '';
  if (peek(P) && peek(P)!.k === '{') {
    P.i++;
    while (P.i < P.toks.length && peek(P)!.k !== '}') {
      const tk = peek(P)!; P.i++;
      if (tk.k === 'char') name += tk.v;
      else if (tk.k === 'cmd') name += tk.v;
    }
    if (peek(P) && peek(P)!.k === '}') P.i++;
  }
  if (name === 'array' && peek(P) && peek(P)!.k === '{') { rawText(P); } // skip column spec {ccc}

  const rows: MNode[][] = [];
  let curRow: MNode[] = [];
  let cell: MNode[] = [];
  const pushCell = () => { curRow.push(row(cell)); cell = []; };
  const pushRow = () => { pushCell(); rows.push(curRow); curRow = []; };

  if (P.depth++ > MAX_DEPTH) { P.depth--; return mtext('…'); }
  while (P.i < P.toks.length && !P.over) {
    const tk = peek(P)!;
    if (tk.k === 'space') { P.i++; continue; }                // skip so a trailing "\\ \end" doesn't leak the env name as a row
    if (tk.k === 'cmd' && tk.v === 'end') {
      P.i++;
      if (peek(P) && peek(P)!.k === '{') rawText(P);          // consume {name}
      break;
    }
    if (tk.k === '&') { P.i++; pushCell(); continue; }
    if (tk.k === 'rowbreak') { P.i++; pushRow(); continue; }
    if (tk.k === '}') break;
    let b = parseAtom(P);
    if (b == null) continue;
    b = attachScripts(P, b);
    cell.push(b);
  }
  P.depth--;
  // flush the final row unless it is a lone trailing empty cell (from a closing \\)
  if (cell.length || curRow.length) pushRow();
  // rows.length guards the last-row access; that row has length 1 here so [0] exists.
  if (rows.length && rows[rows.length - 1]!.length === 1 && isEmpty(rows[rows.length - 1]![0]!)) rows.pop();

  const aligned = name === 'aligned' || name === 'align' || name === 'align*' || name === 'split';
  const mtable = el('mtable', rows.map((r) =>
    el('mtr', r.map((c, ci) => {
      const a: Record<string, string> = {};
      if (name === 'cases') a.columnalign = 'left';
      else if (aligned) a.columnalign = ci % 2 === 0 ? 'right' : 'left';
      return el('mtd', [c], Object.keys(a).length ? a : null);
    }))
  ));
  const [open, close] = ENV_FENCE[name] || ['', ''];
  return open || close ? fenced(open, mtable, close) : mtable;
}
const isEmpty = (d: MNode | undefined) => d && d.tag === 'mrow' && (!d.children || d.children.length === 0);

export function buildMathAst(tex: unknown, { display = false }: { display?: boolean } = {}): MNode {
  const src = String(tex ?? '');
  if (src.length > MAX_TEX) return { tag: 'math', attrs: { display: display ? 'block' : 'inline' }, children: [mtext(src)], over: true };
  const P = parser(lex(src));
  let kids = parseRun(P, false);
  if (P.over) kids = [mtext(src)];                          // hit a bound -> degrade to literal source
  return el('math', [row(kids)], { display: display ? 'block' : 'inline' });
}

// Serialize to a MathML string (pure; for tests / debugging).
const escapeXml = (s: string) => s.replace(/[&<>]/g, (c) => (c === '&' ? '&amp;' : c === '<' ? '&lt;' : '&gt;'));
export function toMathMLString(d: MNode): string {
  if (d.tag && !MML_ELEMENTS.has(d.tag)) throw new Error('smath: element outside allow-list: ' + d.tag);
  const attrs = d.attrs ? Object.keys(d.attrs).filter((k) => k !== 'limits').map((k) => ` ${k}="${escapeXml(String(d.attrs![k]))}"`).join('') : '';
  if (d.text != null) return `<${d.tag}${attrs}>${escapeXml(d.text)}</${d.tag}>`;
  const inner = (d.children || []).map(toMathMLString).join('');
  return `<${d.tag}${attrs}>${inner}</${d.tag}>`;
}

function toDom(d: MNode, doc: Document): Element {
  if (!MML_ELEMENTS.has(d.tag)) throw new Error('smath: element outside allow-list: ' + d.tag);
  const node = doc.createElementNS(MML, d.tag);
  if (d.attrs) for (const k in d.attrs) if (k !== 'limits') node.setAttribute(k, String(d.attrs[k]));
  if (d.text != null) node.appendChild(doc.createTextNode(d.text));
  else if (d.children) for (const c of d.children) node.appendChild(toDom(c, doc));
  return node;
}

// renderMath(tex, {display}) -> a <span> wrapping native <math> (matches the
// .md-math / .md-math-display contract the markdown renderer + base.css expect).
// Total + synchronous: never throws (any failure degrades to literal source text).
export function renderMath(tex: unknown, { display = false, document: docArg }: { display?: boolean; document?: Document } = {}): HTMLSpanElement {
  const doc: Document | null = docArg || (typeof document !== 'undefined' ? document : null);
  if (!doc) throw new Error('smath: no document available; pass options.document.');
  const span = doc.createElement('span');
  span.className = display ? 'md-math md-math-display' : 'md-math';
  try {
    span.appendChild(toDom(buildMathAst(tex, { display }), doc));
  } catch {
    span.textContent = String(tex ?? '');                  // stay total no matter what
  }
  return span;
}

export { MML_ELEMENTS };
export default renderMath;
