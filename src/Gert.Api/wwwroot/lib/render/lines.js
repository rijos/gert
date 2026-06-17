// render/lines.js - block-level line classification + the bounded block parser.
//
// Block classification is ONE declarative, ordered LINE_KINDS table.
// classifyLine(line, lookahead, depth) walks the table once per line and returns
// {kind, ...captures}; the block parser below consumes that single verdict for
// BOTH dispatch AND the paragraph-interrupt, so a line can never be classified
// two different ways. Each block kind's test appears EXACTLY ONCE here, which is
// what makes the 4 documented edge cases resolve to a single, CommonMark-stricter
// answer (pinned by the gallery's FUNCTIONAL cards).
//
// Precedence is FROZEN/asserted below:
//   fence > indent-code > math-$$ > math-\[ > ATX > thematic > blockquote >
//   table-header > list-item > setext  (else: paragraph).
//
// No DOM here; inline parsing (parseInline) is injected through ctx so this module
// depends only on its own block helpers. BOUNDED: container nesting (quote/list)
// is gated by depth < MAX_NEST inside classifyLine itself, so adversarial nesting
// degrades to literal text instead of recursing without bound.

// Bounded pushdown stack cap for block container nesting (quote/list). Mirrors
// markdown.js's MAX_NEST; past the cap a would-be container is NOT classified as
// one (classifyLine returns paragraph), so `>>>>...`x100000 can neither recurse
// to a stack overflow nor allocate without bound.
const MAX_NEST = 32;

// Bounded table dimensions. The body-row loop pads every row UP TO the header
// column count, so a K-column header followed by M short rows allocates K*M AST
// cells from only O(K+M) source bytes - an unbounded-amplification DoS (a few KB
// of source -> 100k+ DOM nodes). MAX_TABLE_COLS clamps width and MAX_TABLE_CELLS
// caps the total; past the cap we keep consuming the table region but stop
// emitting rows (a truncated table), mirroring MAX_NEST/MAX_NODES elsewhere.
// Generous vs any real assistant table (a 20x100 is ~2000 cells).
const MAX_TABLE_COLS = 256;
const MAX_TABLE_CELLS = 8192;

// --- block lexical helpers (all linear, no nested-quantifier regex) ----------
function matchFenceOpen(line) {
  const m = /^( {0,3})(`{3,}|~{3,})(.*)$/.exec(line);
  if (!m) return null;
  if (m[2][0] === "`" && m[3].includes("`")) return null;
  return { char: m[2][0], len: m[2].length, indent: m[1].length, info: m[3].trim() };
}
function isFenceClose(line, ch, len) {
  const m = /^( {0,3})(`{3,}|~{3,})[ \t]*$/.exec(line);
  return !!m && m[2][0] === ch && m[2].length >= len;
}
function isThematicBreak(line) {
  const t = line.trim();
  if (t.length < 3) return false;
  const c = t[0];
  if (c !== "-" && c !== "*" && c !== "_") return false;
  let count = 0;
  for (const ch of t) { if (ch === c) count++; else if (ch !== " ") return false; }
  return count >= 3;
}
function isDelimiterRow(line) {
  if (!/^ {0,3}[|:\- \t]*$/.test(line) || !line.includes("-")) return false;
  const cells = line.trim().replace(/^\|/, "").replace(/\|$/, "").split("|");
  return cells.length > 0 && cells.every((c) => /^:?-+:?$/.test(c.trim()));
}
function splitRowCells(row) {
  let s = row.trim();
  if (s.startsWith("|")) s = s.slice(1);
  if (/(^|[^\\])\|$/.test(s)) s = s.replace(/\|$/, "");
  const cells = []; let buf = "", ticks = 0;
  for (let i = 0; i < s.length; i++) {
    const ch = s[i];
    if (ticks === 0) {
      if (ch === "\\" && i + 1 < s.length) { buf += ch + s[i + 1]; i++; continue; }
      if (ch === "`") { let n = 0; while (s[i + n] === "`") n++; ticks = n; buf += "`".repeat(n); i += n - 1; continue; }
      if (ch === "|") { cells.push(buf.trim()); buf = ""; continue; }
      buf += ch;
    } else {
      if (ch === "`") { let n = 0; while (s[i + n] === "`") n++; if (n === ticks) ticks = 0; buf += "`".repeat(n); i += n - 1; continue; }
      buf += ch;
    }
  }
  cells.push(buf.trim());
  return cells;
}
function cellAlignment(c) {
  const t = c.trim(); const l = t.startsWith(":"), r = t.endsWith(":");
  return l && r ? "center" : r ? "right" : l ? "left" : null;
}
function matchListMarker(line) {
  let m = /^( {0,3})([-+*]|\d{1,9}[.)])( +)(.*)$/.exec(line);
  if (m) { const ind = m[1].length, mk = m[2]; const ord = /\d/.test(mk); return { ordered: ord, start: ord ? parseInt(mk, 10) : 1, contentCol: ind + mk.length + m[3].length, rest: m[4] }; }
  m = /^( {0,3})([-+*]|\d{1,9}[.)])\s*$/.exec(line);
  if (m) { const ind = m[1].length, mk = m[2]; const ord = /\d/.test(mk); return { ordered: ord, start: ord ? parseInt(mk, 10) : 1, contentCol: ind + mk.length + 1, rest: "" }; }
  return null;
}
const TASK_RX = /^\[([ xX])\]\s+(.*)$/; // task-list checkbox at item start

// --- the single declarative classifier --------------------------------------
// One frozen LineKind enum (the kinds classifyLine can return) + one frozen,
// ORDERED LINE_KINDS table. Each row's `test(line, lookahead, depth)` is the
// STRICTER existing dispatcher regex/helper, appearing EXACTLY ONCE, returning a
// captures object (or {} ) on a match and null otherwise. classifyLine walks the
// table top-to-bottom (= precedence) and returns the first hit's {kind, ...caps},
// or {kind: PARAGRAPH}. The block parser uses this for dispatch AND the
// paragraph-interrupt, so the two can never disagree.
export const LineKind = Object.freeze({
  FENCE: "fence",
  INDENT_CODE: "indent_code",
  MATH_DOLLAR: "math_dollar",
  MATH_BRACKET: "math_bracket",
  ATX: "atx",
  THEMATIC: "thematic",
  BLOCKQUOTE: "blockquote",
  TABLE_HEADER: "table_header",
  LIST_ITEM: "list_item",
  SETEXT: "setext",
  PARAGRAPH: "paragraph",
});

const ATX_RX = /^ {0,3}(#{1,6})(?:\s+([^\n]*?))?\s*#*\s*$/;
const MATH_DOLLAR_OPEN_RX = /^ {0,3}\$\$\s*$/;          // prefix-only `$$` (block open)
const MATH_DOLLAR_ONE_RX = /^ {0,3}\$\$(.+?)\$\$\s*$/;  // one-line `$$ ... $$`
const MATH_BRACKET_RX = /^ {0,3}\\\[/;
const SETEXT_RX = /^ {0,3}(=+|-+)[ \t]*$/;

// Ordered precedence table. Frozen so the set/order can't be mutated at runtime;
// classifyLine returns the FIRST matching row, so order IS precedence.
export const LINE_KINDS = Object.freeze([
  Object.freeze({ kind: LineKind.FENCE, test: (line) => matchFenceOpen(line) }),
  Object.freeze({ kind: LineKind.INDENT_CODE, test: (line) => (/^(?: {4}|\t)/.test(line) ? {} : null) }),
  Object.freeze({ kind: LineKind.MATH_DOLLAR, test: (line) => {
    if (MATH_DOLLAR_OPEN_RX.test(line)) return { open: true };
    const m = MATH_DOLLAR_ONE_RX.exec(line);
    return m ? { latex: m[1] } : null;
  } }),
  Object.freeze({ kind: LineKind.MATH_BRACKET, test: (line) => (MATH_BRACKET_RX.test(line) ? { rest: line.replace(MATH_BRACKET_RX, "") } : null) }),
  Object.freeze({ kind: LineKind.ATX, test: (line) => { const m = ATX_RX.exec(line); return m ? { level: m[1].length, text: (m[2] || "").trim() } : null; } }),
  Object.freeze({ kind: LineKind.THEMATIC, test: (line) => (isThematicBreak(line) ? {} : null) }),
  Object.freeze({ kind: LineKind.BLOCKQUOTE, test: (line, _look, depth) => (/^ {0,3}>/.test(line) && depth < MAX_NEST ? {} : null) }),
  Object.freeze({ kind: LineKind.TABLE_HEADER, test: (line, look) => (line.includes("|") && look !== undefined && isDelimiterRow(look) && /^ {0,3}\S/.test(line) ? {} : null) }),
  Object.freeze({ kind: LineKind.LIST_ITEM, test: (line, _look, depth) => { if (depth >= MAX_NEST) return null; return matchListMarker(line); } }),
  Object.freeze({ kind: LineKind.SETEXT, test: (line, look) => {
    if (look === undefined || !SETEXT_RX.test(look) || !line.trim()) return null;
    if (line.includes("|") && isDelimiterRow(look)) return null; // a table header, not a setext text line
    return { level: look.trim()[0] === "=" ? 1 : 2 };
  } }),
]);

// classifyLine(line, lookahead, depth) -> {kind, ...captures}. ONE pass over the
// frozen table; first hit wins (order = precedence). No match -> a paragraph line.
export function classifyLine(line, lookahead, depth) {
  for (const row of LINE_KINDS) {
    const caps = row.test(line, lookahead, depth);
    if (caps) return Object.assign({ kind: row.kind }, caps);
  }
  return { kind: LineKind.PARAGRAPH };
}

// Frozen unit assertion: the LINE_KINDS table IS the dispatcher's precedence, so a
// reorder (or a dropped/added row) is a silent behavior change. Pin the exact order
// at module load - a mismatch throws here rather than skewing classification later.
const LINE_KINDS_ORDER = Object.freeze([
  LineKind.FENCE, LineKind.INDENT_CODE, LineKind.MATH_DOLLAR, LineKind.MATH_BRACKET,
  LineKind.ATX, LineKind.THEMATIC, LineKind.BLOCKQUOTE, LineKind.TABLE_HEADER,
  LineKind.LIST_ITEM, LineKind.SETEXT,
]);
(() => {
  const actual = LINE_KINDS.map((r) => r.kind);
  const ok = actual.length === LINE_KINDS_ORDER.length &&
    actual.every((k, idx) => k === LINE_KINDS_ORDER[idx]);
  if (!ok) throw new Error("render/lines.js: LINE_KINDS precedence drift: " + actual.join(">"));
})();

// --- block parser (bounded recursion = bounded container stack) -------------
// The dispatch switch AND the paragraph-continuation loop both ask classifyLine,
// so the paragraph break-check is never duplicated and a line can't be classified
// two ways. `parseInline` is injected via ctx so this module owns no inline logic.
// `depth` bounds container nesting (MAX_NEST), so adversarial `>`/list nesting
// degrades to text.
export function parseBlocks(lines, ctx, depth) {
  const { parseInline } = ctx;
  const blocks = []; let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (line.trim() === "") { i++; continue; }

    const cls = classifyLine(line, lines[i + 1], depth);

    if (cls.kind === LineKind.FENCE) {
      const fo = cls; const body = []; i++;
      while (i < lines.length && !isFenceClose(lines[i], fo.char, fo.len)) {
        body.push(fo.indent && lines[i].slice(0, fo.indent).trim() === "" ? lines[i].slice(fo.indent) : lines[i]); i++;
      }
      if (i < lines.length) i++;
      blocks.push({ type: "code_block", info: fo.info, literal: body.join("\n") }); continue;
    }

    // indented code block (4+ leading spaces / a tab) at the top level.
    if (cls.kind === LineKind.INDENT_CODE) {
      const body = [];
      while (i < lines.length && (/^(?: {4}|\t)/.test(lines[i]) || !lines[i].trim())) {
        if (!lines[i].trim()) {
          let j = i; while (j < lines.length && !lines[j].trim()) j++;
          if (j >= lines.length || !/^(?: {4}|\t)/.test(lines[j])) break;
          body.push(""); i = j; continue;
        }
        body.push(lines[i].replace(/^(?: {4}|\t)/, "")); i++;
      }
      blocks.push({ type: "code_block", info: "", literal: body.join("\n") }); continue;
    }

    // display math blocks: $$ ... $$ and \[ ... \] (<=3 indent; one-liner or
    // multi-line). An unclosed block falls through to a paragraph (streaming).
    if (cls.kind === LineKind.MATH_DOLLAR) {
      if (cls.latex !== undefined) { blocks.push({ type: "math_block", latex: cls.latex }); i++; continue; }
      const body = []; i++;
      while (i < lines.length && !MATH_DOLLAR_OPEN_RX.test(lines[i])) body.push(lines[i++]);
      if (i < lines.length) i++;
      blocks.push({ type: "math_block", latex: body.join("\n") }); continue;
    }
    if (cls.kind === LineKind.MATH_BRACKET) {
      const rest = cls.rest;
      const one = rest.match(/^(.*?)\\\][ \t]*$/);
      if (one && one[1].trim()) { blocks.push({ type: "math_block", latex: one[1] }); i++; continue; }
      if (!one) {
        const mbody = rest.trim() ? [rest] : []; let k = i + 1, closed = false;
        while (k < lines.length) { const m = lines[k].match(/^(.*?)\\\][ \t]*$/); if (m) { if (m[1].trim()) mbody.push(m[1]); blocks.push({ type: "math_block", latex: mbody.join("\n").trim() }); i = k + 1; closed = true; break; } mbody.push(lines[k]); k++; }
        if (closed) continue;
        // unclosed: fall through and render literally for now
      }
    }

    if (cls.kind === LineKind.ATX) { blocks.push({ type: "heading", level: cls.level, children: parseInline(cls.text, ctx, 0) }); i++; continue; }

    if (cls.kind === LineKind.THEMATIC) { blocks.push({ type: "thematic_break" }); i++; continue; }

    if (cls.kind === LineKind.BLOCKQUOTE) {
      const q = [];
      while (i < lines.length) { const l = lines[i]; if (/^ {0,3}>/.test(l)) { q.push(l.replace(/^ {0,3}> ?/, "")); i++; } else if (l.trim() === "") break; else { q.push(l); i++; } }
      blocks.push({ type: "blockquote", children: parseBlocks(q, ctx, depth + 1) }); continue;
    }

    if (cls.kind === LineKind.TABLE_HEADER) {
      const header = splitRowCells(line); const cols = Math.min(header.length, MAX_TABLE_COLS);
      const align = splitRowCells(lines[i + 1]).map(cellAlignment).slice(0, cols);
      const rows = []; let j = i + 2;
      for (; j < lines.length; j++) {
        const b = lines[j]; if (b.trim() === "" || matchFenceOpen(b)) break;
        // BOUNDED: keep consuming the table region but stop EMITTING rows once the
        // total cell count would exceed the cap (truncated table, never an O(K*M) blowup).
        if ((rows.length + 1) * cols <= MAX_TABLE_CELLS) { const cells = splitRowCells(b); while (cells.length < cols) cells.push(""); rows.push(cells.slice(0, cols)); }
      }
      blocks.push({ type: "table", align, header: header.slice(0, cols), rows }); i = j; continue;
    }

    if (cls.kind === LineKind.LIST_ITEM) {
      const ordered = cls.ordered, start = cls.start, items = []; let tight = true;
      while (i < lines.length) {
        const m = matchListMarker(lines[i]);
        if (!m || m.ordered !== ordered) break;
        const col = m.contentCol; const itemLines = [m.rest]; i++; let blanks = 0;
        while (i < lines.length) {
          const l = lines[i];
          if (l.trim() === "") { itemLines.push(""); blanks++; i++; continue; }
          const indent = l.length - l.replace(/^ +/, "").length;
          if (indent >= col) { itemLines.push(l.slice(col)); i++; continue; }
          break;
        }
        while (itemLines.length && itemLines[itemLines.length - 1] === "") itemLines.pop();
        // task-list checkbox: a leading [ ] / [x] on the first item line.
        let task = false, checked = false;
        const tm = TASK_RX.exec(itemLines[0] || "");
        if (tm) { task = true; checked = tm[1].toLowerCase() === "x"; itemLines[0] = tm[2]; }
        const child = parseBlocks(itemLines, ctx, depth + 1);
        if (child.length > 1) tight = false;
        if (blanks > 0 && matchListMarker(lines[i] || "")) tight = false;
        items.push({ type: "item", task, checked, children: child });
      }
      blocks.push({ type: "list", ordered, start, tight, children: items }); continue;
    }

    if (cls.kind === LineKind.SETEXT) {
      blocks.push({ type: "heading", level: cls.level, children: parseInline(line.trim(), ctx, 0) }); i += 2; continue;
    }

    // paragraph: a run of lines that each classify as a paragraph line. The
    // continuation test is the SAME classifyLine the dispatch above used, so the
    // interrupt and the dispatch can never disagree (one classifier, one verdict).
    const para = [];
    while (i < lines.length) {
      const l = lines[i];
      if (l.trim() === "") break;
      // A bare setext underline (===/---) ends the paragraph; it is then dispatched
      // on its own (thematic break for ---, literal for ===). Setext headings form
      // ONLY at dispatch, from a first line whose successor is an underline - so an
      // INTERIOR paragraph line is never reclassified as setext text by its
      // lookahead. (Without this guard a multi-line paragraph followed by an
      // underline split off its last line into a heading.)
      if (SETEXT_RX.test(l) && para.length) break;
      const k = classifyLine(l, lines[i + 1], depth).kind;
      if (k !== LineKind.PARAGRAPH && k !== LineKind.SETEXT) break;
      para.push(l); i++;
    }
    if (para.length) blocks.push({ type: "paragraph", children: parseInline(para.join("\n"), ctx, 0) });
    else i++;
  }
  return blocks;
}
