// render/dom.js - the STRUCTURAL renderer: markdown AST -> DOM. Moved verbatim
// out of lib/markdown.js (which is now a thin facade). math & code leaves are
// OPAQUE: this module CALLS the VanJS components MdMath / MdCode and inserts the
// DOM they return - it never reaches into smath/highlight itself.
//
// Security F4: every markdown element this renderer emits is built through ONE
// guarded chokepoint - createEl(ns, tag, attrs, doc) - over a CLOSED per-(ns,tag)
// allow-list with a fail-closed throw. The allow-list pins which attributes each
// tag may carry (href only on <a>, src only on <img>); td/th alignment is a CSSOM
// write (el.style.textAlign), NOT an attribute. No innerHTML anywhere: nodes are
// built with createElement / createTextNode only, so injected HTML in the source
// renders as literal text. URLs are scrubbed at the link/image call sites through
// sanitizeUrl / sanitizeImgUrl (sink-side, local); external links get
// rel="noopener noreferrer" target="_blank".
//
// renderNode has a branch per node type and a `default: throw`, so the producible
// node set is exactly NODE_TYPES (frozen, re-exported). The math/code leaves'
// DOM shape is whatever MdMath / MdCode return (the same span/<math> and
// <pre data-lang><code> trees as before), so existing selectors/CSS/consumers and
// the byte-oracle all still hold.
import { sanitizeUrl, sanitizeImgUrl, isExternal, slugify } from "./url.js";
import { decodeEntities, flattenText } from "./inline.js";
import { MdMath } from "/components/canvas/artifacts/md-math.js";
import { MdCode } from "/components/canvas/artifacts/md-code.js";

// --- the renderer's AST node shape ------------------------------------------
// The producer side is split across two parsers - render/inline.js builds the
// inline nodes, render/lines.js builds the BLOCK nodes (document/heading/list/
// table/...) - and neither exports a node type the renderer can import (the inline
// InlineNode bag is module-local; the block nodes are untyped Record<string,
// unknown>). renderNode reads a `node.type` discriminated switch across BOTH, so -
// mirroring the Stage 1 Tok/InlineNode pattern - we declare ONE wide node bag here:
// every field optional, discriminated by `type`, listing exactly the fields
// renderNode (and its helpers) read off a node and nothing the code doesn't touch.
interface Node {
  type: string;
  children?: Node[] | undefined;
  // block fields
  level?: number | undefined;
  ordered?: boolean | undefined;
  start?: number | undefined;
  tight?: boolean | undefined;
  info?: string | undefined;
  literal?: string | undefined;
  // list-item fields
  task?: boolean | undefined;
  checked?: boolean | undefined;
  // table fields
  header?: string[] | undefined;
  align?: (string | null | undefined)[] | undefined;
  rows?: string[][] | undefined;
  // inline / leaf fields (shared with InlineNode)
  value?: string | undefined;
  latex?: string | undefined;
  display?: boolean | undefined;
  dest?: string | undefined;
  title?: string | null | undefined;
  alt?: string | undefined;
}

// The render context lib/markdown.js wires: the DOM target + the inline parser it
// injects (render/dom.js re-enters parseInline for table cells). parseInline's
// signature mirrors render/inline.js's export; its returned inline-node list feeds
// renderInlineList (typed Node[] here - the same wide bag, of which an inline node
// is a structural instance).
interface Ctx {
  doc: Document;
  parseInline: (raw: string, ctx: Ctx, depth: number) => Node[];
}

// The closed node set the parser can produce. renderNode has a branch for each
// and throws on anything else (fail closed), so the emitted DOM is a fixed
// allow-list. Frozen so the set can't be mutated at runtime. (Re-exported by
// lib/markdown.js with identity preserved.)
const NODE_TYPES = Object.freeze([
  "document", "heading", "paragraph", "blockquote", "list", "item",
  "code_block", "thematic_break", "table", "math_block",
  "text", "code", "emph", "strong", "del", "link", "image", "math_inline",
  "linebreak", "softbreak",
]);

// MAX_INLINE bounds INLINE container nesting (emph/strong/del/link) at the
// (sink-side) renderer: past the cap a would-be emph/strong/del/link degrades to
// its flattened text - a finite configuration space, so adversarial nesting can't
// recurse to a stack overflow. (Block container nesting is capped by MAX_NEST
// inside render/lines.js, where the block parser lives.)
const MAX_INLINE = 32;

// --- the single guarded element chokepoint ----------------------------------
// createEl(ns, tag, attrs, doc) is the ONLY way this renderer produces an
// element. ALLOW maps each (ns, tag) the structural renderer may emit to the
// CLOSED set of attribute names it may carry; an unknown (ns, tag) OR an
// attribute outside that tag's set is a fail-closed throw (caught by the facade's
// literal-source fallback). So the producible DOM is a fixed allow-list: href can
// only appear on <a>, src only on <img>, and no element can grow an event-handler
// or style attribute through this path. ns "" is HTML (createElement). td/th
// alignment is a CSSOM write (el.style.textAlign) applied by the caller, NOT an
// attribute, so it never passes through here.
const HTML = "";
const ALLOW = Object.freeze({
  "": Object.freeze({
    table: [], thead: [], tbody: [], tr: [], th: [], td: [],
    h1: ["id"], h2: ["id"], h3: ["id"], h4: ["id"], h5: ["id"], h6: ["id"],
    p: [], blockquote: [], div: ["class"],
    ol: ["start", "class"], ul: ["class"], li: ["class"],
    input: ["type", "disabled", "checked"],
    hr: [], code: [], em: [], strong: [], del: [], br: [],
    a: ["href", "title", "target", "rel"],
    img: ["src", "alt", "title"],
  }),
});

function createEl(ns: string, tag: string, attrs: Record<string, string> | null, doc: Document): HTMLElement {
  // ALLOW is the frozen closed allow-list (Object.freeze, identity-preserved). The
  // (ns, tag) lookup is over dynamic strings, so index it through a narrow read-only
  // map view; the value is still the per-tag attribute-name array (or undefined),
  // and the existing `&&` guard + `!allowed` fail-closed throw are unchanged.
  const allowList = ALLOW as Readonly<Record<string, Readonly<Record<string, readonly string[]>> | undefined>>;
  const allowed = allowList[ns] && allowList[ns]![tag];
  if (!allowed) throw new Error("markdown: disallowed element: " + (ns || "html") + ":" + tag);
  // ALLOW only ever holds the HTML namespace (ns === ""), so the createElement
  // branch is the live path (returns HTMLElement); the createElementNS branch is
  // kept for the generic chokepoint shape but unreachable here, so narrow its
  // Element result to HTMLElement to keep the return type (and callers' .style /
  // .classList use) unchanged. This is type-only - the runtime call is identical.
  const el: HTMLElement = ns ? doc.createElementNS(ns, tag) as HTMLElement : doc.createElement(tag);
  if (attrs) {
    for (const name in attrs) {
      if (!allowed.includes(name)) throw new Error("markdown: disallowed attr " + name + " on " + tag);
      // `name` is an own enumerable key of `attrs` (for..in), so attrs[name] is a
      // defined string under noUncheckedIndexedAccess; assert that loop invariant.
      el.setAttribute(name, attrs[name]!);
    }
  }
  return el;
}

// --- total renderer: node -> DOM (closed set; default throws) ----------------
// `depth` bounds INLINE container nesting (emph/strong/del/link): past MAX_INLINE
// the node degrades to its flattened text. Block nesting is already bounded in the
// parser (MAX_NEST), so renderBlockList needs no counter. Without this cap a wall
// of balanced delimiters (e.g. "*"x20000 ... "*"x20000) nests deep enough to blow
// the call stack - renderMarkdown must stay TOTAL.
function renderInlineList(nodes: Node[], ctx: Ctx, depth = 0) { const f = ctx.doc.createDocumentFragment(); for (const n of nodes) f.appendChild(renderNode(n, ctx, depth)); return f; }
function renderBlockList(nodes: Node[], ctx: Ctx) { const f = ctx.doc.createDocumentFragment(); for (const n of nodes) f.appendChild(renderNode(n, ctx)); return f; }

function renderTable(t: Node, ctx: Ctx) {
  const { doc } = ctx, table = createEl(HTML, "table", null, doc);
  const cell = (tag: string, raw: string | undefined, align: string | null | undefined) => { const el = createEl(HTML, tag, null, doc); el.appendChild(renderInlineList(ctx.parseInline(raw || "", ctx, 0), ctx)); if (align) el.style.textAlign = align; return el; };
  const thead = createEl(HTML, "thead", null, doc), htr = createEl(HTML, "tr", null, doc);
  // `t` is a table node, so header/align/rows are present by construction in
  // render/lines.js; assert that tag invariant (matching the parser's producer).
  t.header!.forEach((h, c) => htr.appendChild(cell("th", h, t.align![c]))); thead.appendChild(htr); table.appendChild(thead);
  const tbody = createEl(HTML, "tbody", null, doc);
  for (const row of t.rows!) { const tr = createEl(HTML, "tr", null, doc); row.forEach((cv, c) => tr.appendChild(cell("td", cv, t.align![c]))); tbody.appendChild(tr); }
  table.appendChild(tbody); return table;
}

function renderCodeBlock(node: Node, ctx: Ctx) {
  // The code leaf is the MdCode component: it builds <pre data-lang><code>…</code></pre>
  // (highlight() tints from textContent; data-lang guarded to a short slice). The
  // component adopts a stylesheet on first render; in an environment lacking
  // Constructable Stylesheets that could throw BEFORE the leaf renders, so degrade
  // to an un-tinted <pre><code> (per-block totality) rather than failing the doc.
  const { doc } = ctx;
  const lang = (node.info || "").split(/[ \t]/, 1)[0];
  try { return MdCode({ code: node.literal, lang }); }
  catch {
    const pre = createEl(HTML, "pre", null, doc), code = createEl(HTML, "code", null, doc);
    // a code_block node always carries `literal` (built so in render/lines.js);
    // assert it so textContent (string | null) accepts the assignment unchanged.
    code.textContent = node.literal!; pre.appendChild(code); return pre;
  }
}

// math leaves are VanJS components with the same first-render stylesheet-adoption
// caveat: degrade a failed formula to its literal TeX (per-formula totality - the
// contract is that bad math degrades per-formula, never the whole document).
function mathLeaf(latex: string | undefined, display: boolean, doc: Document) {
  // a math node always carries `latex` (built so in render/lines.js / inline.js);
  // assert it so createTextNode (string) sees a string, byte-identical to before.
  try { return MdMath({ latex, display }); } catch { return doc.createTextNode(latex!); }
}

function renderNode(node: Node, ctx: Ctx, depth = 0) {
  const { doc } = ctx;
  // Each branch reads exactly the fields its `node.type` tag guarantees (the parser
  // in render/lines.js / inline.js builds the node so): a container always carries
  // `children`, a heading `level`, a text node `value`, a code node `literal`, a
  // math node `latex`. The `!`s below assert that tag invariant (the closed switch +
  // `default: throw` keep the producible set == NODE_TYPES), never hide an absence.
  switch (node.type) {
    case "document": return renderBlockList(node.children!, ctx);
    case "heading": { const h = createEl(HTML, "h" + Math.min(6, Math.max(1, node.level!)), null, doc); h.appendChild(renderInlineList(node.children!, ctx)); return h; }
    case "paragraph": { const p = createEl(HTML, "p", null, doc); p.appendChild(renderInlineList(node.children!, ctx)); return p; }
    case "blockquote": { const b = createEl(HTML, "blockquote", null, doc); b.appendChild(renderBlockList(node.children!, ctx)); return b; }
    case "list": {
      const el = createEl(HTML, node.ordered ? "ol" : "ul", node.ordered && node.start !== 1 ? { start: String(node.start) } : null, doc);
      let hasTask = false;
      for (const item of node.children!) {
        const li = createEl(HTML, "li", null, doc);
        if (item.task) {
          hasTask = true; li.classList.add("task-list-item");
          const box = createEl(HTML, "input", item.checked ? { type: "checkbox", disabled: "", checked: "" } : { type: "checkbox", disabled: "" }, doc);
          li.appendChild(box); li.appendChild(doc.createTextNode(" "));
        }
        for (const child of item.children!) {
          if (node.tight && child.type === "paragraph") li.appendChild(renderInlineList(child.children!, ctx));
          else li.appendChild(renderNode(child, ctx));
        }
        el.appendChild(li);
      }
      if (hasTask) el.classList.add("contains-task-list");
      return el;
    }
    case "code_block": return renderCodeBlock(node, ctx);
    case "thematic_break": return createEl(HTML, "hr", null, doc);
    case "table": return renderTable(node, ctx);
    case "math_block": { const div = createEl(HTML, "div", { class: "md-math-block" }, doc); div.appendChild(mathLeaf(node.latex, true, doc)); return div; }

    case "text": return doc.createTextNode(decodeEntities(node.value!));
    case "code": { const c = createEl(HTML, "code", null, doc); c.textContent = node.literal!; return c; }
    case "emph": { if (depth >= MAX_INLINE) return doc.createTextNode(flattenText(node.children!)); const e = createEl(HTML, "em", null, doc); e.appendChild(renderInlineList(node.children!, ctx, depth + 1)); return e; }
    case "strong": { if (depth >= MAX_INLINE) return doc.createTextNode(flattenText(node.children!)); const s = createEl(HTML, "strong", null, doc); s.appendChild(renderInlineList(node.children!, ctx, depth + 1)); return s; }
    case "del": { if (depth >= MAX_INLINE) return doc.createTextNode(flattenText(node.children!)); const d = createEl(HTML, "del", null, doc); d.appendChild(renderInlineList(node.children!, ctx, depth + 1)); return d; }
    case "math_inline": return mathLeaf(node.latex, !!node.display, doc);
    case "linebreak": return createEl(HTML, "br", null, doc);
    case "softbreak": return doc.createTextNode("\n");

    case "link": {
      if (depth >= MAX_INLINE) return doc.createTextNode(flattenText(node.children!));
      const href = sanitizeUrl(node.dest!);
      // attrs typed Record<string,string> so the title/target/rel keys are CONDITIONALLY
      // added (never assigned undefined) - the exact same shape as before, so the closed
      // ALLOW attribute check in createEl ("a": href/title/target/rel) is unchanged (F4).
      const attrs: Record<string, string> = { href };
      if (node.title) attrs.title = decodeEntities(node.title);
      if (isExternal(href)) { attrs.target = "_blank"; attrs.rel = "noopener noreferrer"; }
      const a = createEl(HTML, "a", attrs, doc);
      a.appendChild(renderInlineList(node.children!, ctx, depth + 1));
      return a;
    }
    case "image": {
      // same conditional-add shape; ALLOW "img": src/alt/title is unchanged (F4).
      const attrs: Record<string, string> = { src: sanitizeImgUrl(node.dest!), alt: decodeEntities(node.alt || "") };
      if (node.title) attrs.title = decodeEntities(node.title);
      return createEl(HTML, "img", attrs, doc);
    }
    default: throw new Error("markdown: unhandled node type: " + node.type);
  }
}

// --- heading anchors --------------------------------------------------------
// GitHub-style slug `id` on every heading so in-document links ([x](#section))
// resolve. slugify (./url.js) folds the heading TEXT to a `[a-z0-9_-]` token, so
// the id is inert even via setAttribute. Duplicate slugs get -1/-2 (GFM), unique
// within this fragment. Runs as a DOM post-pass (reads textContent after render),
// so it sees the fully built tree.
const assignHeadingIds = (frag: DocumentFragment) => {
  const used = new Set<string>();
  for (const h of frag.querySelectorAll("h1,h2,h3,h4,h5,h6")) {
    // an Element always has non-null textContent (null is only doctype/document);
    // assert it so slugify (string) sees a string, byte-identical to the JS original.
    const base = slugify(h.textContent!) || "section";
    let id = base, n = 0;
    while (used.has(id)) id = `${base}-${++n}`;
    used.add(id);
    h.setAttribute("id", id);
  }
};

// render(ast, ctx) -> DocumentFragment for the document node. The facade wires
// ctx { doc, parseInline } and runs assignHeadingIds afterwards.
const render = (ast: Node, ctx: Ctx) => renderNode(ast, ctx);

export { render, assignHeadingIds, NODE_TYPES };
