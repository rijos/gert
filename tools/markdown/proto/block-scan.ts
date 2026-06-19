// proto/block-scan.ts - EXPLORATION PROTOTYPE (not production).
//
// The design question: "would the BLOCK lexer be cleaner as a per-CHARACTER,
// regex-free state machine instead of the per-LINE, regex-driven classifier in
// render/lines.js?" This is the honest, runnable answer.
//
// It is a per-character, ZERO-regex block scanner producing the SAME block-AST
// node shapes the production parser emits ({type:'heading'|'paragraph'|
// 'code_block'|'thematic_break'|'blockquote', ...}), reusing the production
// parseInline for inline content. proto/compare.ts renders both this and the
// production parser and diffs them on the corpus.
//
// WHAT THIS DEMONSTRATES (the point of the exploration):
//   * Per-character + regex-free is entirely FEASIBLE for the block layer, and
//     for the line-uniform constructs (heading / hr / fence / paragraph / one
//     level of blockquote) the code is comparable in size and clearly bounded.
//   * BUT a "block" boundary in Markdown is defined PER LINE (a fence opens on a
//     line; a paragraph ends at a blank line; indentation columns decide list
//     continuation). So even a "per-character" scanner spends most of its logic
//     re-discovering line starts and counting leading indentation -- i.e. it
//     re-implements "what line am I on" by hand. The machine gets BIGGER, not
//     smaller, exactly where the per-line classifier is small (lists / tables /
//     setext lookahead are deliberately omitted here to show the cliff).
//
// Conclusion (matches README section 3): per-line is the right granularity for a
// line-defined grammar; keep the production block lexer per-line. Per-character
// is the right granularity for INTRA-line work, which the renderer already does
// in the inline + math lexers.

// ---- block-AST node shapes (mirror render/lines.js's emitted node shapes) ----
// `parseInline` is the injected production inline parser; its result is an opaque
// inline-node array this file only ever stashes into `children` and never walks.
type InlineNodes = readonly unknown[];
type ParseInline = (text: string, ctx: { parseInline: ParseInline }, depth: number) => InlineNodes;

type HeadingBlock = { type: "heading"; level: number; children: InlineNodes };
type ParagraphBlock = { type: "paragraph"; children: InlineNodes };
type CodeBlock = { type: "code_block"; info: string; literal: string };
type ThematicBreakBlock = { type: "thematic_break" };
type BlockquoteBlock = { type: "blockquote"; children: Block[] };
type Block = HeadingBlock | ParagraphBlock | CodeBlock | ThematicBreakBlock | BlockquoteBlock;

// ---- a tiny character cursor (no regex anywhere in this file) ---------------
class Cursor {
  s: string;
  i: number;
  n: number;
  constructor(s: string) { this.s = s; this.i = 0; this.n = s.length; }
  eof() { return this.i >= this.n; }
  peek(k = 0) { return this.s[this.i + k]; }
  // read to end of line (exclusive of '\n'); advance past the '\n'
  readLine() {
    let j = this.i;
    while (j < this.n && this.s[j] !== "\n") j++;
    const line = this.s.slice(this.i, j);
    this.i = j < this.n ? j + 1 : j; // consume newline
    return line;
  }
}

// ---- char-class predicates (replacing the per-line regexes) -----------------
const isSpace = (c: string | undefined) => c === " " || c === "\t";
function leadingSpaces(line: string) { let n = 0; while (n < line.length && line[n] === " ") n++; return n; }
function isBlank(line: string) { for (const c of line) if (!isSpace(c)) return false; return true; }

// ATX heading: up to 3 spaces, 1-6 '#', then space or EOL. Returns {level,text} or null.
function atx(line: string): { level: number; text: string } | null {
  let i = 0; while (i < 3 && line[i] === " ") i++;
  let h = 0; while (line[i + h] === "#") h++;
  if (h < 1 || h > 6) return null;
  const after = line[i + h];
  if (after !== undefined && !isSpace(after)) return null;
  // strip trailing ' #...' and surrounding spaces, by hand
  let rest = line.slice(i + h);
  let a = 0; while (a < rest.length && isSpace(rest[a])) a++;
  let b = rest.length; while (b > a && isSpace(rest[b - 1])) b--;
  rest = rest.slice(a, b);
  let e = rest.length; while (e > 0 && rest[e - 1] === "#") e--;
  if (e < rest.length) { while (e > 0 && isSpace(rest[e - 1])) e--; rest = rest.slice(0, e); }
  return { level: h, text: rest };
}

// thematic break: >=3 of the same {- * _}, only spaces between.
function thematic(line: string): boolean {
  let i = 0; while (i < 3 && line[i] === " ") i++;
  const c = line[i];
  if (c !== "-" && c !== "*" && c !== "_") return false;
  let count = 0;
  for (; i < line.length; i++) { const ch = line[i]; if (ch === c) count++; else if (ch !== " ") return false; }
  return count >= 3;
}

// fence open: up to 3 spaces, then >=3 of ` or ~. Returns {char,len,indent,info} or null.
function fenceOpen(line: string): { char: string; len: number; indent: number; info: string } | null {
  let i = 0; while (i < 3 && line[i] === " ") i++;
  const c = line[i];
  if (c !== "`" && c !== "~") return null;
  let len = 0; while (line[i + len] === c) len++;
  if (len < 3) return null;
  const info = line.slice(i + len).trim();
  if (c === "`" && info.includes("`")) return null; // backtick fence info can't contain backtick
  return { char: c, len, indent: i, info };
}
function fenceClose(line: string, ch: string, len: number): boolean {
  let i = 0; while (i < 3 && line[i] === " ") i++;
  if (line[i] !== ch) return false;
  let n = 0; while (line[i + n] === ch) n++;
  if (n < len) return false;
  for (let k = i + n; k < line.length; k++) if (!isSpace(line[k])) return false;
  return true;
}

// blockquote marker: up to 3 spaces, '>', optional one space. Returns the rest or null.
function quoteStrip(line: string): string | null {
  let i = 0; while (i < 3 && line[i] === " ") i++;
  if (line[i] !== ">") return null;
  i++; if (line[i] === " ") i++;
  return line.slice(i);
}

// ---- the per-character block scanner ----------------------------------------
// parseInline is injected (same contract as the production block lexer).
export function parseBlocksPerChar(src: unknown, parseInline: ParseInline, depth = 0): Block[] {
  const MAX_NEST = 32;
  const cur = new Cursor(String(src).replace(/\r\n?/g, "\n").replace(/\t/g, "    "));
  const blocks: Block[] = [];

  while (!cur.eof()) {
    const start = cur.i;
    const line = cur.readLine();

    if (isBlank(line)) continue;

    // fenced code: scan raw lines until the matching close (per-character within,
    // but the boundary is unavoidably a per-LINE decision -> we readLine()).
    const fo = fenceOpen(line);
    if (fo) {
      const body: string[] = [];
      while (!cur.eof()) {
        const l = cur.readLine();
        if (fenceClose(l, fo.char, fo.len)) break;
        body.push(fo.indent && leadingSpaces(l) >= fo.indent ? l.slice(fo.indent) : l);
      }
      blocks.push({ type: "code_block", info: fo.info, literal: body.join("\n") });
      continue;
    }

    const h = atx(line);
    if (h) { blocks.push({ type: "heading", level: h.level, children: parseInline(h.text, { parseInline }, 0) }); continue; }

    if (thematic(line)) { blocks.push({ type: "thematic_break" }); continue; }

    if (depth < MAX_NEST && quoteStrip(line) !== null) {
      // accumulate the quote's raw lines, then recurse (one level shown).
      // INVARIANT: the enclosing guard just proved quoteStrip(line) !== null for this
      // same pure input, so this re-call is non-null too (tsgo can't track across calls).
      const q: string[] = [quoteStrip(line)!];
      while (!cur.eof()) {
        const save = cur.i; void save; const l = cur.readLine(); // `save` is captured but
        // (unlike the paragraph loop) never rewound here; `void save` keeps the original
        // dead binding under noUnusedLocals - inert read, mirrors the file's `void start;`.
        const stripped = quoteStrip(l);
        if (stripped !== null) { q.push(stripped); continue; }
        if (isBlank(l)) break;
        q.push(l); // lazy continuation
      }
      blocks.push({ type: "blockquote", children: parseBlocksPerChar(q.join("\n"), parseInline, depth + 1) });
      continue;
    }

    // paragraph: accumulate non-blank lines that are not a new block start.
    const para: string[] = [line];
    while (!cur.eof()) {
      const save = cur.i; const l = cur.readLine();
      if (isBlank(l) || atx(l) || thematic(l) || fenceOpen(l) || quoteStrip(l) !== null) { cur.i = save; break; }
      para.push(l);
    }
    blocks.push({ type: "paragraph", children: parseInline(para.join("\n"), { parseInline }, 0) });

    // NOTE: lists, tables, setext, indented code, math blocks are INTENTIONALLY
    // omitted. They are exactly the constructs whose boundaries are defined by
    // line lookahead + indentation columns, where a per-character machine must
    // re-derive line structure by hand -- the cliff this prototype exists to show.
    void start;
  }
  return blocks;
}
