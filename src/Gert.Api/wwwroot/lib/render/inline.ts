// render/inline.js - the inline half of the markdown parser (single linear pass).
// Pure: no DOM, no side effects; render/lines.js and lib/markdown.js import
// parseInline (and decodeEntities/flattenText) from here.
//
// BOUNDED: the inline emphasis/bracket nesting stacks are hard-capped, and every
// scan (math closer, link destination/title, fenced inline code) is O(cap) per
// opener - no nested-quantifier / catastrophic-backtrack regex - so adversarial
// nesting/walls of delimiters degrade to literal text instead of blowing up.

// Inline-math closer scan cap. Real inline math is short; the cap keeps a wall of
// unmatched "$" / "\(" from going quadratic (long display math lives in $$ / \[
// BLOCKS, parsed line-by-line). Mirrors the link MAX_DEST defense.
const MATH_INLINE_MAX = 1024;
// Link destination / title scan caps: a real destination is never thousands of
// chars; the cap keeps an unmatched "](" wall O(cap) per opener, not O(n^2).
const MAX_DEST = 1024;
const MAX_TITLE = 512;

// The inline parser threads two loosely-typed bags through a single linear pass:
// `Tok` is a scanner token (discriminated by `t`) that doubles as a node in the
// emphasis-pairing doubly-linked list (prev/next), and `InlineNode` is the AST node
// (discriminated by `type`) consumed by render/dom.js. Both are written here as wide
// optional-field bags rather than strict discriminated unions: the scanner mutates
// tokens in place (op.n -= use, toks[k] = ...) and the linked-list head is a sentinel,
// so a strict union would force runtime-changing restructuring (forbidden). Field
// presence is governed by the `t`/`type` tag exactly as the original JS assumed.
// Optional fields are widened with an explicit `| undefined` because tokToNode builds
// these objects by copying optional Tok fields straight through (so a key may be present
// with an undefined value) - exactOptionalPropertyTypes requires the union to admit it.
interface InlineNode {
  type: string;
  value?: string | undefined;
  literal?: string | undefined;
  latex?: string | undefined;
  display?: boolean | undefined;
  dest?: string | undefined;
  title?: string | null | undefined;
  alt?: string | undefined;
  children?: InlineNode[] | undefined;
}
interface Tok {
  t: string;
  v?: string | undefined;
  ch?: string | undefined;
  n?: number | undefined;
  image?: boolean | undefined;
  display?: boolean | undefined;
  dest?: string | undefined;
  title?: string | null | undefined;
  text?: string | undefined;
  type?: string | undefined;
  alt?: string | undefined;
  canOpen?: boolean | undefined;
  canClose?: boolean | undefined;
  children?: InlineNode[] | undefined;
  // doubly-linked-list wiring used only by finalizeEmphasis:
  prev?: Tok | null | undefined;
  next?: Tok | null | undefined;
}
// The render context injected by lib/markdown.js (doc + the inline parser itself,
// re-entered by render/dom.js for table cells). `doc`/`parseInline` are the only
// fields this module reads; typed wider via index signature for the untyped JS caller.
interface InlineCtx {
  doc?: Document;
  parseInline?: (text: string, ctx: InlineCtx, depth: number) => InlineNode[];
  [k: string]: unknown;
}

// Decode the entity references CommonMark resolves in text. We never feed source
// to the HTML parser (F4), so we decode a small named set + numeric refs by hand
// into real characters; an unknown/malformed entity is left verbatim.
const NAMED_ENTITIES: Record<string, string> = {
  amp: "&", lt: "<", gt: ">", quot: '"', apos: "'", nbsp: " ",
  copy: "©", reg: "®", trade: "™", hellip: "…",
  mdash: "—", ndash: "–", laquo: "«", raquo: "»",
  deg: "°", plusmn: "±", times: "×", divide: "÷",
  larr: "←", rarr: "→", uarr: "↑", darr: "↓",
  harr: "↔", check: "✓", cross: "✗", bull: "•",
};
const decodeEntities = (s: string) => {
  if (s.indexOf("&") === -1) return s;
  return s.replace(/&(#x[0-9a-f]+|#\d+|[a-z][a-z0-9]*);/gi, (m: string, body: string) => {
    if (body[0] === "#") {
      const cp = body[1] === "x" || body[1] === "X"
        ? parseInt(body.slice(2), 16)
        : parseInt(body.slice(1), 10);
      if (!Number.isFinite(cp) || cp <= 0 || cp > 0x10ffff || (cp >= 0xd800 && cp <= 0xdfff))
        return m;
      try { return String.fromCodePoint(cp); } catch { return m; }
    }
    const named = NAMED_ENTITIES[body.toLowerCase()];
    return named !== undefined ? named : m;
  });
};

const AUTOLINK_ANGLE = /^<([a-z][a-z0-9+.-]*:[^<>\s]+|[^<>\s@]+@[^<>\s]+)>/i;
const URL_RX = /^(https?:\/\/|www\.)[^\s<]+/i;
const EMAIL_RX = /^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+/;

const PUNCT = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

function tokenizeInline(text: string, _ctx: InlineCtx, _depth: number): Tok[] {
  const toks: Tok[] = []; const brackets: { idx: number; image: boolean }[] = []; let i = 0, buf = "";
  const flush = () => { if (buf) { toks.push({ t: "text", v: buf }); buf = ""; } };
  const N = text.length;
  while (i < N) {
    // i < N === text.length holds (loop condition), so text[i] is a defined char.
    const ch = text[i]!;
    if (ch === "\\") {
      const nx = text[i + 1];
      if (nx === "\n") { flush(); toks.push({ t: "br" }); i += 2; continue; }
      // \(...\) inline LaTeX math. (\[...\] display math is block-level, so a
      // mid-line "\[escaped\]" stays a literal bracket escape - finding #13.)
      if (nx === "(") {
        const cs = i + 2;
        const rel = text.slice(cs, cs + MATH_INLINE_MAX).indexOf("\\)");
        if (rel >= 0) { flush(); toks.push({ t: "math", display: false, v: text.slice(cs, cs + rel) }); i = cs + rel + 2; continue; }
      }
      if (nx && PUNCT.includes(nx)) { buf += nx; i += 2; continue; }
      buf += "\\"; i++; continue;
    }
    if (ch === "`") {
      let n = 0; while (text[i + n] === "`") n++;
      let j = i + n, found = -1;
      while (j < N) { if (text[j] === "`") { let m = 0; while (text[j + m] === "`") m++; if (m === n) { found = j; break; } j += m; } else j++; }
      if (found >= 0) {
        flush(); let c = text.slice(i + n, found).replace(/\n/g, " ");
        if (c.length > 1 && c[0] === " " && c[c.length - 1] === " " && c.trim() !== "") c = c.slice(1, -1);
        toks.push({ t: "code", v: c }); i = found + n; continue;
      }
      buf += "`".repeat(n); i += n; continue;
    }
    if (ch === "$") {
      const disp = text[i + 1] === "$", delim = disp ? "$$" : "$", start = i + delim.length;
      const cap = start + MATH_INLINE_MAX;
      let j = start, found = -1;
      while (j < N && j <= cap) { if (text[j] === "\\") { j += 2; continue; } if (disp ? (text[j] === "$" && text[j + 1] === "$") : text[j] === "$") { found = j; break; } j++; }
      if (found > start) {
        let ok = true;
        if (!disp) { const a = text[start], b = text[found - 1], af = text[found + 1]; if (a === " " || a === "\t" || b === " " || b === "\t" || (af !== undefined && af >= "0" && af <= "9")) ok = false; }
        if (ok) { flush(); toks.push({ t: "math", display: disp, v: text.slice(start, found) }); i = found + delim.length; continue; }
      }
      buf += delim; i += delim.length; continue;
    }
    if (ch === "!" && text[i + 1] === "[") { flush(); brackets.push({ idx: toks.length, image: true }); toks.push({ t: "obracket", image: true }); i += 2; continue; }
    if (ch === "[") { flush(); brackets.push({ idx: toks.length, image: false }); toks.push({ t: "obracket", image: false }); i++; continue; }
    if (ch === "]") {
      if (brackets.length === 0) { buf += "]"; i++; continue; }
      flush();
      // brackets.length !== 0 checked above, so pop() returns a defined entry.
      const open = brackets.pop()!; const oIdx = open.idx;
      const tail = text[i + 1] === "(" ? parseLinkTail(text, i + 1) : null;
      if (tail) {
        const label = toks.slice(oIdx + 1);
        toks.length = oIdx;
        // brackets.length truthiness guards the access, so the top entry exists.
        while (brackets.length && brackets[brackets.length - 1]!.idx >= oIdx) brackets.pop();
        const children = finalizeEmphasis(label);
        if (open.image) toks.push({ t: "image", dest: tail.dest, title: tail.title, alt: flattenText(children) });
        else toks.push({ t: "link", dest: tail.dest, title: tail.title, children });
        i = tail.end; continue;
      }
      toks[oIdx] = { t: "text", v: open.image ? "![" : "[" };
      buf += "]"; i++; continue;
    }
    if (ch === "~" && text[i + 1] === "~") {
      const prev = text[i - 1] || "", next = text[i + 2] || "";
      const pWS = prev === "" || /\s/.test(prev), nWS = next === "" || /\s/.test(next);
      flush(); toks.push({ t: "delim", ch: "~", n: 2, canOpen: !nWS, canClose: !pWS }); i += 2; continue;
    }
    if (ch === "*" || ch === "_") {
      let n = 0; while (text[i + n] === ch) n++;
      const prev = text[i - 1] || "", next = text[i + n] || "";
      const pWS = prev === "" || /\s/.test(prev), nWS = next === "" || /\s/.test(next);
      const pP = prev !== "" && PUNCT.includes(prev), nP = next !== "" && PUNCT.includes(next);
      const left = !nWS && (!nP || pWS || pP);
      const right = !pWS && (!pP || nWS || nP);
      let canOpen, canClose;
      if (ch === "_") { canOpen = left && (!right || pP); canClose = right && (!left || nP); }
      else { canOpen = left; canClose = right; }
      flush(); toks.push({ t: "delim", ch, n, canOpen, canClose }); i += n; continue;
    }
    if (ch === "<") {
      const m = AUTOLINK_ANGLE.exec(text.slice(i));
      if (m) {
        // AUTOLINK_ANGLE has one capture group, so a match always defines m[1] and m[0].
        const inner = m[1]!;
        const href = inner.indexOf("@") !== -1 && inner.indexOf(":") === -1 ? "mailto:" + inner : inner;
        flush(); toks.push({ t: "autolink", dest: href, text: inner }); i += m[0]!.length; continue;
      }
      buf += "<"; i++; continue; // never HTML
    }
    if (ch === "\n") { if (/ {2,}$/.test(buf)) { buf = buf.replace(/ +$/, ""); flush(); toks.push({ t: "br" }); } else { flush(); toks.push({ t: "sb" }); } i++; continue; }
    // bare / www / email autolink (GFM), only at a word boundary.
    const prevCh = text[i - 1];
    const atBoundary = prevCh === undefined || /[\s(<]/.test(prevCh);
    if (atBoundary && (ch === "h" || ch === "w" || ch === "H" || ch === "W")) {
      const um = URL_RX.exec(text.slice(i));
      if (um) {
        // um[0] is the whole match (defined whenever um is non-null).
        let rawu = um[0]!.replace(/[!"'.,:;?*_~]+$/, "");
        let opens = 0; for (const c of rawu) { if (c === "(") opens++; else if (c === ")") opens--; }
        while (opens < 0 && rawu.endsWith(")")) { rawu = rawu.slice(0, -1); opens++; }
        const href = /^www\./i.test(rawu) ? "http://" + rawu : rawu;
        flush(); toks.push({ t: "autolink", dest: href, text: rawu }); i += rawu.length; continue;
      }
    }
    if (atBoundary && /[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]/.test(ch)) {
      const em = EMAIL_RX.exec(text.slice(i));
      // em[0] is the whole match (defined whenever em is non-null).
      if (em && !/^www\./i.test(em[0]!)) {
        const nx = text[i + em[0]!.length];
        if (nx === undefined || /[\s).,;:!?]/.test(nx)) { flush(); toks.push({ t: "autolink", dest: "mailto:" + em[0]!, text: em[0]! }); i += em[0]!.length; continue; }
      }
    }
    buf += ch; i++;
  }
  flush();
  // k < toks.length guards every access, so toks[k] exists.
  for (let k = 0; k < toks.length; k++) if (toks[k]!.t === "obracket") toks[k] = { t: "text", v: toks[k]!.image ? "![" : "[" };
  return toks;
}

function parseLinkTail(text: string, pos: number): { dest: string; title: string | null; end: number } | null {
  if (text[pos] !== "(") return null;
  let j = pos + 1, dest = "", title: string | null = null;
  while (text[j] === " " || text[j] === "\t" || text[j] === "\n") j++;
  // Inside each `j < text.length` loop, text[j] is a defined char (asserted on append).
  if (text[j] === "<") { j++; while (j < text.length && text[j] !== ">") { if (text[j] === "\n") return null; if (text[j] === "\\") { dest += text[j + 1] || ""; j += 2; continue; } dest += text[j++]!; if (dest.length > MAX_DEST) return null; } if (text[j] !== ">") return null; j++; }
  else { let paren = 0; while (j < text.length) { const c = text[j]!; if (c === "\\") { dest += c + (text[j + 1] || ""); j += 2; continue; } if (c === " " || c === "\t" || c === "\n") break; if (c === "(") { paren++; dest += c; j++; continue; } if (c === ")") { if (paren === 0) break; paren--; dest += c; j++; continue; } dest += c; j++; if (dest.length > MAX_DEST) return null; } }
  while (text[j] === " " || text[j] === "\t" || text[j] === "\n") j++;
  if (text[j] === '"' || text[j] === "'" || text[j] === "(") { const o = text[j], cl = o === "(" ? ")" : o; j++; while (j < text.length && text[j] !== cl) { if (text[j] === "\\") { title = (title || "") + (text[j + 1] || ""); j += 2; continue; } title = (title || "") + text[j++]!; if (title.length > MAX_TITLE) return null; } if (text[j] !== cl) return null; j++; }
  while (text[j] === " " || text[j] === "\t" || text[j] === "\n") j++;
  if (text[j] !== ")") return null;
  return { dest, title, end: j + 1 };
}

function tokToNode(tok: Tok): InlineNode {
  // The fields read in each branch are exactly those the matching `t` tag guarantees
  // (a "built" tok always carries `type`; a "delim" always carries `ch`+`n`); the `!`
  // asserts that tag invariant rather than hiding a genuine absence.
  switch (tok.t) {
    case "built": return { type: tok.type!, children: tok.children };
    case "text": return { type: "text", value: tok.v };
    case "code": return { type: "code", literal: tok.v };
    case "math": return { type: "math_inline", latex: tok.v, display: tok.display };
    case "link": return { type: "link", dest: tok.dest, title: tok.title, children: tok.children };
    case "autolink": return { type: "link", dest: tok.dest, title: null, children: [{ type: "text", value: tok.text }] };
    case "image": return { type: "image", dest: tok.dest, title: tok.title, alt: tok.alt };
    case "obracket": return { type: "text", value: tok.image ? "![" : "[" };
    case "br": return { type: "linebreak" };
    case "sb": return { type: "softbreak" };
    case "delim": return { type: "text", value: tok.ch!.repeat(tok.n!) };
    default: return { type: "text", value: "" };
  }
}

// Emphasis/strike pairing via a delimiter stack over a doubly-linked list.
function finalizeEmphasis(toks: Tok[]): InlineNode[] {
  // The list nodes are Tok bags wired with prev/next; the `t:"head"` node is a sentinel.
  // Inside the `node.t === "delim"` arm every delim field (ch/n/canOpen/canClose) is
  // present by construction in tokenizeInline, so the `!`s below assert that invariant.
  const head: Tok = { t: "head", next: null, prev: null }; let prev: Tok = head;
  for (const t of toks) { const n: Tok = Object.assign({}, t, { prev, next: null }); prev.next = n; prev = n; }
  const remove = (x: Tok) => { x.prev!.next = x.next; if (x.next) x.next.prev = x.prev; };
  const openers: Tok[] = []; let node: Tok | null | undefined = head.next;
  while (node) {
    if (node.t === "delim") {
      if (node.canClose && node.n! > 0) {
        let oi = -1;
        for (let k = openers.length - 1; k >= 0; k--) { const o = openers[k]!; if (o.ch === node.ch && o.n! > 0 && o.canOpen) { oi = k; break; } }
        if (oi >= 0) {
          const op = openers[oi]!;
          const strike = node.ch === "~";
          const use = strike ? 2 : (op.n! >= 2 && node.n! >= 2) ? 2 : 1;
          if (strike && (op.n! < 2 || node.n! < 2)) { if (node.canOpen && node.n! > 0) openers.push(node); node = node.next; continue; }
          const inner: Tok[] = []; let c = op.next; while (c && c !== node) { inner.push(c); c = c.next; }
          const type = strike ? "del" : use === 2 ? "strong" : "emph";
          const wrap: Tok = { t: "built", type, children: inner.map(tokToNode), prev: null, next: null };
          op.next = wrap; wrap.prev = op; wrap.next = node; node.prev = wrap;
          op.n = op.n! - use; node.n = node.n! - use;
          openers.splice(oi + 1);
          if (op.n === 0) { openers.splice(oi, 1); remove(op); }
          if (node.n === 0) { const nx = node.next; remove(node); node = nx; continue; }
          continue;
        }
      }
      if (node.canOpen && node.n! > 0) openers.push(node);
      node = node.next; continue;
    }
    node = node.next;
  }
  const out: InlineNode[] = []; let p = head.next;
  while (p) {
    const n = tokToNode(p); const last = out[out.length - 1];
    // Both nodes are text (type==="text"), so each carries a string value; assert it
    // so the concatenation stays string += string (byte-identical to the original).
    if (n.type === "text" && last && last.type === "text") last.value = last.value! + n.value!;
    else out.push(n);
    p = p.next;
  }
  return out;
}

export function parseInline(text: string, ctx: InlineCtx, depth: number): InlineNode[] { return finalizeEmphasis(tokenizeInline(text, ctx, depth)); }
// Flatten an inline node list to its text (link/image labels). ITERATIVE (an
// explicit work-stack, in document order) so a pathologically deep emphasis tree
// - e.g. a "*"x20000 image label - can't blow the call stack: renderMarkdown
// must stay total. O(total nodes).
function flattenText(nodes: InlineNode[]): string {
  let s = "";
  const stack: { list: InlineNode[]; i: number }[] = [{ list: nodes, i: 0 }];
  while (stack.length) {
    // stack.length truthiness guards the top access; the i>=length check guards the next.
    const top = stack[stack.length - 1]!;
    if (top.i >= top.list.length) { stack.pop(); continue; }
    const n = top.list[top.i++]!;
    if (n.type === "text") s += n.value;
    else if (n.type === "code") s += n.literal;
    else if (n.type === "math_inline") s += n.latex;
    else if (n.type === "image") s += n.alt || "";
    else if (n.children) stack.push({ list: n.children, i: 0 });
  }
  return s;
}

export { decodeEntities, flattenText };
