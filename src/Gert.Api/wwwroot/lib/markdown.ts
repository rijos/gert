// markdown.js - THIN FACADE over the in-house markdown renderer (no npm, no build
// step). The pipeline is split across render/*:
//
//   normalize -> parseBlocks (render/lines.js, the LINE_KINDS classifier)
//             -> render      (render/dom.js, the structural renderer + the single
//                             guarded createEl allow-list; calls MdMath / MdCode)
//             -> assignHeadingIds (render/dom.js, DOM post-pass)
//
// all inside ONE try/catch with a literal-source fallback so renderMarkdown stays
// TOTAL (HARD CONTRACT 3). The inline scanner lives in render/inline.js, the URL/
// slug safety helpers in render/url.js, the math/code leaves are the VanJS
// components MdMath / MdCode (render/dom.js calls them). This module owns no
// parsing or rendering itself - it wires ctx and re-exports the public surface.
//
// Security F4: NO raw HTML is ever interpreted. The structural renderer builds
// every node with createElement / createTextNode through ONE guarded createEl
// chokepoint over a closed per-(ns,tag) allow-list (render/dom.js), so injected
// HTML/<script> in the source renders as literal text. URLs are scrubbed through
// the single sanitizeUrl chokepoint (render/url.js); images allow ONLY inline
// data:image of known-safe media types; MathML (MdMath/smath) keeps its own closed
// MML allow-list, and fenced code (MdCode/highlight) emits only inert tok-* spans.
// Math is native <math> MathML - zero third-party code in the model-output path.
//
// Covers the CommonMark/GFM subset the assistant + md artifacts emit: headings
// (ATX + setext), bold/italic/strike/code, links/images/autolinks, nested +
// ordered + task lists, blockquotes (recursive, depth-capped), fenced + indented
// code, GFM tables, hard breaks, backslash escapes, HTML entities, and math
// ($..$ / $$..$$ and \(..\) / \[..\]). Returns a DocumentFragment, never a string.
import { sanitizeUrl } from "./render/url.js";
import { parseBlocks } from "./render/lines.js";
import { parseInline } from "./render/inline.js";
import { render, assignHeadingIds, NODE_TYPES } from "./render/dom.js";

// --- public API -------------------------------------------------------------
// renderMarkdown(src) -> DocumentFragment of sanitized nodes. Synchronous,
// null-safe, total: any input (half-open fence, unbalanced emphasis mid-stream)
// yields a complete fragment without throwing.
export const renderMarkdown = (src: unknown) => {
  // `source` is declared out here but NORMALIZED inside the try: String(src) can
  // itself throw (an object whose toString() throws), and renderMarkdown must stay
  // TOTAL for ANY input, not just strings. If normalization faults, source stays
  // "" and the catch yields an empty fragment - never a throw into the caller.
  let source = "";
  try {
    source = String(src ?? "").replace(/\r\n?/g, "\n").replace(/\t/g, "    ");
    // ctx carries the DOM target + the inline parser injected into the block
    // parser (render/lines.js owns no inline logic) and the structural renderer
    // (render/dom.js calls ctx.parseInline for table cells).
    const ctx = { doc: document, parseInline };
    // ctx / ast / frag cross between the independently-typed render/* stage modules
    // (render/lines.js's BlockCtx + Block, render/dom.js's Node + Document target),
    // whose node/ctx bags are declared per-module and don't structurally unify. These
    // are the facade's AST/DOM boundary casts (type-only): the runtime wiring - the
    // SAME ctx object, the SAME { type: "document", children } ast, the SAME fragment
    // through assignHeadingIds - is byte-identical to the original JS.
    const ast = { type: "document", children: parseBlocks(source.split("\n"), ctx as Parameters<typeof parseBlocks>[1], 0) };
    const frag = render(ast as unknown as Parameters<typeof render>[0], ctx as unknown as Parameters<typeof render>[1]);
    assignHeadingIds(frag as DocumentFragment);
    return frag;
  } catch {
    // Belt-and-suspenders: the parser/renderer are bounded and total by design,
    // but any unforeseen fault must still degrade to the literal source rather
    // than throw into the caller's VanJS render loop (which would wedge the view).
    const f = document.createDocumentFragment();
    f.appendChild(document.createTextNode(source));
    return f;
  }
};

// sanitizeUrl re-exported from ./render/url.js, NODE_TYPES from ./render/dom.js
// (identity preserved for both).
export { sanitizeUrl, NODE_TYPES };
