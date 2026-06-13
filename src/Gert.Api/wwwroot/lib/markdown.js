// markdown.js - tiny vendored markdown renderer + sanitizer (no npm).
// Security F4: NO raw HTML is ever interpreted. Everything is built as real
// DOM nodes (textContent), so injected HTML/<script> renders as literal text.
// URLs are scrubbed (javascript:/data:/vbscript: rejected); external links get
// rel="noopener noreferrer" target="_blank".
//
// This is intentionally small: it covers the constructs the assistant + md
// artifacts use (headings, bold/italic/code, links, lists, blockquote, fenced
// code, GFM tables, paragraphs). It produces nodes, never an HTML string. Fenced code is
// tinted by lib/highlight.js (same node-only stance; the fence info string
// picks the language, with a cheap sniff as fallback).
import { highlight } from "./highlight.js";

// --- URL safety -------------------------------------------------------------
const SAFE_SCHEME = /^(https?:|mailto:|\/|#)/i;
const sanitizeUrl = (raw) => {
  const url = (raw || "").trim();
  // strip control chars/whitespace that smuggle "java\nscript:"
  const collapsed = url.replace(/[\x00-\x1f\x7f\s]/g, "");
  if (/^(javascript|data|vbscript):/i.test(collapsed)) return "#";
  if (SAFE_SCHEME.test(url) || /^[^:]*$/.test(collapsed.split("/")[0])) return url;
  return "#";
};

// http(s) AND protocol-relative "//host" - both leave the app origin, so both
// get target=_blank rel="noopener noreferrer" (a single "/" path stays internal).
const isExternal = (url) => /^https?:/i.test(url) || /^\/\//.test(url);

// --- inline parsing (returns an array of DOM nodes) -------------------------
// Order matters: code spans first so their contents aren't re-parsed.
const inline = (text) => {
  const out = [];
  let rest = text;
  // token regex: code | bold | italic | link
  const rx =
    /(`[^`]+`)|(\*\*[^*]+\*\*)|(__[^_]+__)|(\*[^*]+\*)|(_[^_]+_)|(\[[^\]]+\]\([^)]+\))/;
  while (rest.length) {
    const m = rx.exec(rest);
    if (!m) {
      out.push(document.createTextNode(rest));
      break;
    }
    if (m.index > 0) out.push(document.createTextNode(rest.slice(0, m.index)));
    const tok = m[0];
    if (tok.startsWith("`")) {
      const c = document.createElement("code");
      c.textContent = tok.slice(1, -1);
      out.push(c);
    } else if (tok.startsWith("**") || tok.startsWith("__")) {
      const s = document.createElement("strong");
      s.append(...inline(tok.slice(2, -2)));
      out.push(s);
    } else if (tok.startsWith("[")) {
      const lm = /^\[([^\]]+)\]\(([^)]+)\)$/.exec(tok);
      const a = document.createElement("a");
      const href = sanitizeUrl(lm[2]);
      a.setAttribute("href", href);
      if (isExternal(href)) {
        a.setAttribute("target", "_blank");
        a.setAttribute("rel", "noopener noreferrer");
      }
      a.append(...inline(lm[1]));
      out.push(a);
    } else {
      const e = document.createElement("em");
      e.append(...inline(tok.slice(1, -1)));
      out.push(e);
    }
    rest = rest.slice(m.index + tok.length);
  }
  return out;
};

// --- GFM tables ---------------------------------------------------------------
// split a `|`-delimited row into trimmed cells (edge pipes optional, \| escaped)
const splitRow = (line) =>
  line
    .trim()
    .replace(/^\|/, "")
    .replace(/\|$/, "")
    .split(/(?<!\\)\|/)
    .map((c) => c.trim().replace(/\\\|/g, "|"));

// the |---|:---:|---:| separator row that marks the line above as a header
const isTableDelim = (line) =>
  line.includes("-") && line.includes("|") && splitRow(line).every((c) => /^:?-+:?$/.test(c));

// --- block parsing ----------------------------------------------------------
// renderMarkdown(src) -> DocumentFragment of sanitized nodes.
export const renderMarkdown = (src) => {
  const frag = document.createDocumentFragment();
  const lines = String(src ?? "").replace(/\r\n?/g, "\n").split("\n");
  let i = 0;

  const flushList = (ordered, items) => {
    const list = document.createElement(ordered ? "ol" : "ul");
    for (const it of items) {
      const li = document.createElement("li");
      li.append(...inline(it));
      list.appendChild(li);
    }
    frag.appendChild(list);
  };

  while (i < lines.length) {
    let line = lines[i];

    if (!line.trim()) {
      i++;
      continue;
    }

    // fenced code (``` or ```lang or ```lang name=file). The fence is THREE OR
    // MORE backticks (CommonMark); the close must repeat at least as many, so a
    // fenced block can carry its own ``` blocks (a Markdown snippet with code in
    // it) without the first inner fence ending it early.
    const fence = /^(`{3,})/.exec(line);
    if (fence) {
      const ticks = fence[1].length;
      const info = line.slice(ticks).trim();
      const closes = (l) => new RegExp("^`{" + ticks + ",}[ \\t]*$").test(l);
      const buf = [];
      i++;
      while (i < lines.length && !closes(lines[i])) buf.push(lines[i++]);
      i++; // closing fence

      // Whole files now reach the canvas through the make_artifact tool, not
      // named fences - so every fence here renders inline as a code block.
      const pre = document.createElement("pre");
      const code = document.createElement("code");
      const lang = info.split(/[ \t]/, 1)[0];
      code.append(...highlight(buf.join("\n"), lang));
      // surface the fence language for chrome (message.js code-head label).
      // Model-controlled, so keep it to a short identifier-ish slice; it only
      // ever lands in textContent/dataset - never parsed as HTML.
      if (/^[\w+#.-]{1,16}$/.test(lang)) pre.dataset.lang = lang.toLowerCase();
      pre.appendChild(code);
      frag.appendChild(pre);
      continue;
    }

    // heading
    const h = /^(#{1,6})\s+(.*)$/.exec(line);
    if (h) {
      const el = document.createElement("h" + h[1].length);
      el.append(...inline(h[2]));
      frag.appendChild(el);
      i++;
      continue;
    }

    // blockquote
    if (/^>\s?/.test(line)) {
      const buf = [];
      while (i < lines.length && /^>\s?/.test(lines[i]))
        buf.push(lines[i++].replace(/^>\s?/, ""));
      const bq = document.createElement("blockquote");
      bq.append(...inline(buf.join(" ")));
      frag.appendChild(bq);
      continue;
    }

    // unordered list
    if (/^[-*+]\s+/.test(line)) {
      const items = [];
      while (i < lines.length && /^[-*+]\s+/.test(lines[i]))
        items.push(lines[i++].replace(/^[-*+]\s+/, ""));
      flushList(false, items);
      continue;
    }

    // ordered list
    if (/^\d+\.\s+/.test(line)) {
      const items = [];
      while (i < lines.length && /^\d+\.\s+/.test(lines[i]))
        items.push(lines[i++].replace(/^\d+\.\s+/, ""));
      flushList(true, items);
      continue;
    }

    // GFM table: a header row with pipes followed by a delimiter row. Cells go
    // through inline() like any other text, so the no-raw-HTML stance holds.
    if (line.includes("|") && i + 1 < lines.length && isTableDelim(lines[i + 1])) {
      const aligns = splitRow(lines[i + 1]).map((c) =>
        /^:-+:$/.test(c) ? "center" : /^-+:$/.test(c) ? "right" : null,
      );
      const row = (cells, tag) => {
        const tr = document.createElement("tr");
        cells.forEach((cell, ci) => {
          const el = document.createElement(tag);
          if (aligns[ci]) el.style.textAlign = aligns[ci];
          el.append(...inline(cell));
          tr.appendChild(el);
        });
        return tr;
      };
      const table = document.createElement("table");
      const thead = document.createElement("thead");
      thead.appendChild(row(splitRow(line), "th"));
      const tbody = document.createElement("tbody");
      i += 2; // header + delimiter
      while (i < lines.length && lines[i].trim() && lines[i].includes("|"))
        tbody.appendChild(row(splitRow(lines[i++]), "td"));
      table.append(thead, tbody);
      frag.appendChild(table);
      continue;
    }

    // paragraph (gather until blank line)
    const buf = [line];
    i++;
    while (
      i < lines.length &&
      lines[i].trim() &&
      !/^(#{1,6}\s|>|[-*+]\s|\d+\.\s|```)/.test(lines[i]) &&
      // stop when the next line opens a table (its successor is a delimiter row)
      !(lines[i].includes("|") && i + 1 < lines.length && isTableDelim(lines[i + 1]))
    )
      buf.push(lines[i++]);
    const p = document.createElement("p");
    p.append(...inline(buf.join(" ")));
    frag.appendChild(p);
  }

  return frag;
};

export { sanitizeUrl };
