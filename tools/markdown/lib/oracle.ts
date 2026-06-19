// oracle.ts - the property oracle for the markdown renderer. Given the DOM
// fragment renderMarkdown() returns, these checks decide whether an input has
// broken a renderer CONTRACT. They are the executable form of the renderer's
// stated guarantees (lib/markdown.js header + docs/design/security.md F4):
//
//   1. TOTAL        renderMarkdown never throws (checked by the caller; here we
//                   also confirm it returns a fragment).
//   2. SECURE (F4)  only allow-list elements; href/src scrubbed; no on* handler;
//                   no <script>/<style>/<iframe>; no `style` attribute; math
//                   carries no href/src/onerror sink and forges no <a>/<img>.
//   3. BOUNDED      output node-count and element depth stay within a ceiling
//                   that is a function of input size (no amplification blowup).
//   4. WELL-FORMED  parent links are consistent and the tree is acyclic.
//   5. DETERMINISTIC  same input -> structurally identical output (no leaked
//                   module state: component's injected-set, regex lastIndex, ...).
//
// Pure + dependency-free; operates on the shim nodes from lib/dom-shim.ts (and,
// because it only reads nodeType / localName / attributes / childNodes, on real
// browser nodes too).

const ELEMENT_NODE = 1;
const TEXT_NODE = 3;
const DOCUMENT_FRAGMENT_NODE = 11;

// Structural view of the nodes the oracle walks. It reads only these members, and
// reads them off BOTH the dom-shim nodes (lib/dom-shim.ts) AND, in principle, real
// browser nodes - so this is a deliberately loose, read-only shape. Element-only
// and text-only members are optional and guarded by nodeType at each use site. The
// renderer hands the oracle `unknown` (see node-shims.d.ts); callers narrow to this.
interface RenderedNode {
  nodeType: number;
  // element members (present when nodeType === ELEMENT_NODE)
  localName?: string;
  namespaceURI?: string | null;
  className?: string;
  attributes?: ReadonlyArray<{ name: string; value: string }>;
  getAttribute?(name: string): string | null;
  // text members (present when nodeType === TEXT_NODE)
  data?: string;
  // tree links (childNodes absent on leaves / text nodes)
  childNodes?: RenderedNode[];
  parentNode?: RenderedNode | null;
}

// Coerce the unknown root the renderer returns into the read-only node view. The
// oracle only ever READS the members above, all guarded by nodeType, so a single
// boundary cast here keeps the rest of the file precisely typed without `any`.
function asNode(n: unknown): RenderedNode {
  return n as RenderedNode;
}

// The closed element allow-list the structural renderer + leaves can emit.
// HTML (createEl ALLOW) + the smath MathML set + the highlight wrappers.
const ALLOWED_HTML = new Set([
  "table", "thead", "tbody", "tr", "th", "td",
  "h1", "h2", "h3", "h4", "h5", "h6",
  "p", "blockquote", "div", "ol", "ul", "li", "input",
  "hr", "code", "em", "strong", "del", "br", "a", "img",
  "pre", "span", // pre/code/span from MdCode; span wrapper from MdMath
]);
const ALLOWED_MML = new Set([
  "math", "mrow", "mi", "mn", "mo", "mtext", "mspace",
  "msup", "msub", "msubsup", "mover", "munder", "munderover",
  "mfrac", "msqrt", "mroot", "mtable", "mtr", "mtd",
]);

// Per-tag attribute allow-list for the HTML the renderer emits (mirrors
// render/dom.js ALLOW + the leaves: pre[data-lang], span[class], input checkbox).
const ATTR_ALLOW: Record<string, Set<string>> = {
  a: new Set(["href", "title", "target", "rel"]),
  img: new Set(["src", "alt", "title"]),
  ol: new Set(["start", "class"]),
  ul: new Set(["class"]),
  li: new Set(["class"]),
  div: new Set(["class"]),
  span: new Set(["class"]),
  pre: new Set(["data-lang", "class"]),
  code: new Set(["class"]),
  input: new Set(["type", "disabled", "checked"]),
  h1: new Set(["id"]), h2: new Set(["id"]), h3: new Set(["id"]),
  h4: new Set(["id"]), h5: new Set(["id"]), h6: new Set(["id"]),
};
// MathML presentation attributes smath may set (inert; no href/src sink).
// mathcolor carries a charset-validated colour from \color/\textcolor - a
// presentation attribute, never an inline style (see lib/smath.js applyColor).
const MML_ATTR = new Set([
  "display", "mathvariant", "stretchy", "fence", "accent", "displaystyle",
  "width", "linethickness", "largeop", "movablelimits", "lspace", "rspace",
  "columnalign", "class", "mathcolor",
]);

const SAFE_IMG = /^(#|data:image\/(?:png|jpe?g|gif|webp|avif|bmp|x-icon);base64,)/i;

// An href is dangerous iff a BROWSER would resolve it to an executing/fetching
// scheme. We mirror the renderer's own collapse (strip controls/whitespace, fold
// an entity-encoded colon) and then extract the RFC-3986 scheme: a scheme exists
// only when [a-zA-Z][a-zA-Z0-9+.-]* is immediately followed by ':'. No valid
// scheme => a RELATIVE url (e.g. "a(b)</x>](data:..." has '(' before any ':', so
// it is NOT a scheme) => safe. A scheme that is not http(s)/mailto is something
// the renderer is supposed to neutralize to "#"; if it survived, F4 was bypassed.
function dangerousHref(href: string): boolean {
  const collapsed = String(href).replace(/[\x00-\x1f\x7f\s]/g, "").replace(/&#0*58;|&#x0*3a;|&colon;/gi, ":");
  const m = /^([a-zA-Z][a-zA-Z0-9+.-]*):/.exec(collapsed);
  // group 1 is mandatory in this regex: it always captures when `m` matches.
  return !!m && !/^(https?|mailto)$/i.test(m[1]!);
}

// --- tree walking ------------------------------------------------------------
export function* walk(node: unknown): Generator<RenderedNode> {
  const stack: RenderedNode[] = [asNode(node)];
  while (stack.length) {
    // invariant: guarded by stack.length above, so pop() yields a node.
    const n = stack.pop()!;
    yield n;
    const kids = n.childNodes;
    if (kids) for (let i = kids.length - 1; i >= 0; i--) {
      // invariant: i is in [0, kids.length), so kids[i] is defined.
      stack.push(kids[i]!);
    }
  }
}
export function elements(root: unknown): RenderedNode[] { return [...walk(root)].filter((n) => n.nodeType === ELEMENT_NODE); }
export function attrsOf(el: RenderedNode): Array<[string, string]> {
  // works on both shim nodes (el.attributes -> [{name,value}]) and a plain map.
  return (el.attributes || []).map((a): [string, string] => [a.name, a.value]);
}

// serialize(node) -> HTML-ish string (void elements self-close). For snapshots,
// crash repros, and corpus expectations - NOT a security boundary.
const VOID = new Set(["br", "hr", "img", "input"]);
export function serialize(nodeArg: unknown): string {
  const node = asNode(nodeArg);
  if (node.nodeType === TEXT_NODE) {
    return String(node.data).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  }
  // invariant: fragments and elements always carry a childNodes array (dom-shim);
  // serialize is only reached for those node kinds.
  if (node.nodeType === DOCUMENT_FRAGMENT_NODE) return node.childNodes!.map(serialize).join("");
  const tag = node.localName;
  let attrs = "";
  for (const [n, v] of attrsOf(node)) attrs += ` ${n}="${String(v).replace(/"/g, "&quot;")}"`;
  if (VOID.has(tag!) && node.childNodes!.length === 0) return `<${tag}${attrs}>`;
  return `<${tag}${attrs}>${node.childNodes!.map(serialize).join("")}</${tag}>`;
}

// --- the contract checks (each returns null on pass, or a violation string) ---
function isMml(el: RenderedNode): boolean { return !!el.namespaceURI && el.namespaceURI.includes("Math/MathML"); }
function inMath(el: RenderedNode): boolean { let p: RenderedNode | null | undefined = el; while (p) { if (p.localName === "span" && (p.className || "").includes("md-math")) return true; p = p.parentNode; } return false; }

export function checkSecurity(root: unknown): string[] | null {
  const viol: string[] = [];
  for (const el of elements(root)) {
    const tag = el.localName!.toLowerCase();
    const mml = isMml(el);
    // 2a. element allow-list
    if (mml) { if (!ALLOWED_MML.has(tag)) viol.push(`mml element not allowed: ${tag}`); }
    else if (!ALLOWED_HTML.has(tag)) viol.push(`html element not allowed: ${tag}`);
    // 2b. attribute allow-list + no on*/style sink
    for (const [name] of attrsOf(el)) {
      const lname = name.toLowerCase();
      if (lname.startsWith("on")) viol.push(`event-handler attr ${name} on ${tag}`);
      if (lname === "style") viol.push(`style attribute on ${tag}`);
      if (mml) { if (!MML_ATTR.has(lname)) viol.push(`mml attr not allowed: ${name} on ${tag}`); }
      else {
        const set = ATTR_ALLOW[tag];
        // class is allowed wherever the leaves set it (span/code/pre/li/ul/ol/div)
        if (lname === "class") continue;
        if (!set || !set.has(lname)) viol.push(`attr not allowed: ${name} on ${tag}`);
      }
    }
    // 2c. href / src scrubbing
    // invariant: elements() filtered to ELEMENT_NODE, so getAttribute is present.
    if (tag === "a") {
      const href = el.getAttribute!("href");
      if (href != null && dangerousHref(href)) viol.push(`unsafe href scheme: ${JSON.stringify(href)}`);
    }
    if (tag === "img") {
      const src = el.getAttribute!("src");
      if (src != null && !SAFE_IMG.test(src.trim())) viol.push(`unsafe img src: ${JSON.stringify(src)}`);
    }
    // 2d. no <a>/<img> forged from inside math, and no sink attr there
    if (inMath(el) && (tag === "a" || tag === "img")) viol.push(`forged <${tag}> inside math`);
  }
  return viol.length ? viol : null;
}

interface CheckBoundsOpts {
  maxNodesPerChar?: number;
  minFloor?: number;
  depthFloor?: number;
}
export function checkBounds(root: unknown, srcLen: number, opts: CheckBoundsOpts = {}): string | null {
  const maxNodesPer = opts.maxNodesPerChar ?? 60; // generous amplification ceiling
  const minFloor = opts.minFloor ?? 2000;
  // Depth is bounded by input size: you cannot nest deeper than you spend input
  // characters (~1 char per nesting level). So the real contract is depth <= srcLen,
  // not a fixed cap - and the genuine bug we want to catch is AMPLIFICATION, where a
  // tiny input yields a huge tree. (NB: smath sub/sup chains "$$___...$$" legitimately
  // reach depth ~= srcLen before MAX_NODES degrades them; that is bounded, not a bug -
  // see tools/markdown/README.md "smath script-chain depth".)
  const depthCeiling = Math.max(opts.depthFloor ?? 512, srcLen + 32);
  let count = 0, deepest = 0;
  const stack: Array<[RenderedNode, number]> = [[asNode(root), 0]];
  while (stack.length) {
    // invariant: guarded by stack.length above, so pop() yields a [node, depth] pair.
    const [n, d] = stack.pop()!;
    count++;
    if (d > deepest) deepest = d;
    const kids = n.childNodes;
    if (kids) for (const k of kids) stack.push([k, d + 1]);
  }
  const ceiling = Math.max(minFloor, srcLen * maxNodesPer);
  if (count > ceiling) return `node count ${count} exceeds ceiling ${ceiling} (srcLen ${srcLen})`;
  if (deepest > depthCeiling) return `tree depth ${deepest} exceeds ${depthCeiling} (amplification; srcLen ${srcLen})`;
  return null;
}

export function checkWellFormed(root: unknown): string | null {
  const seen = new Set<RenderedNode>();
  for (const n of walk(root)) {
    if (seen.has(n)) return "cycle detected in DOM tree";
    seen.add(n);
    if (n.childNodes) for (const k of n.childNodes) {
      if (k.parentNode !== n) return `parent link mismatch for ${k.localName || "#text"}`;
    }
  }
  return null;
}

// structural fingerprint (tag + attrs + text), order-sensitive; for determinism.
export function fingerprint(node: unknown): string { return serialize(node); }

export { ELEMENT_NODE, TEXT_NODE, DOCUMENT_FRAGMENT_NODE };
