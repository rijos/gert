// dom-shim.ts - a tiny, zero-dependency DOM just large enough to run Gert's
// in-house markdown renderer headlessly in Node (no jsdom, no npm). The real
// renderer (src/Gert.Api/wwwroot/lib/markdown.js and its render/* + smath +
// highlight + component leaves) is loaded UNMODIFIED via lib/loader.ts; this
// shim supplies the handful of DOM globals those modules touch:
//
//   document.createElement / createElementNS / createTextNode /
//            createDocumentFragment / adoptedStyleSheets
//   Element : appendChild/append, set/get/hasAttribute, className/classList,
//            style (CSSOM-style object), dataset, textContent, childNodes,
//            querySelector/querySelectorAll (small CSS subset)
//   CSSStyleSheet (replaceSync), Node (ELEMENT_NODE/TEXT_NODE/...)
//
// It is deliberately faithful only where the renderer relies on real semantics
// (fragment-flattening on appendChild, textContent recursion, the heading
// querySelector used by assignHeadingIds). Everything else is the minimum that
// keeps the renderer's own code paths honest. The oracle layer (lib/oracle.ts)
// walks these nodes directly rather than leaning on querySelector, so the shim's
// selector engine only has to be good enough for the renderer itself.

const ELEMENT_NODE = 1;
const TEXT_NODE = 3;
const DOCUMENT_FRAGMENT_NODE = 11;

export const Node = Object.freeze({
  ELEMENT_NODE,
  TEXT_NODE,
  DOCUMENT_FRAGMENT_NODE,
});

// A node in the shim tree: a text node or one of the two parent-node kinds.
// The renderer (and the oracle that walks its output) only ever sees these.
export type DomNode = TextNode | Element | DocumentFragment;

// --- text node ---------------------------------------------------------------
class TextNode {
  nodeType: number;
  nodeName: string;
  data: string;
  parentNode: ParentNode | null;
  constructor(data: unknown) {
    this.nodeType = TEXT_NODE;
    this.nodeName = "#text";
    this.data = String(data);
    this.parentNode = null;
  }
  get textContent(): string { return this.data; }
  set textContent(v: unknown) { this.data = String(v); }
  get nodeValue(): string { return this.data; }
  set nodeValue(v: unknown) { this.data = String(v); }
}

// --- classList over a backing token string -----------------------------------
class ClassList {
  _el: Element;
  constructor(el: Element) { this._el = el; }
  _tokens(): string[] { return this._el._className ? this._el._className.split(/\s+/).filter(Boolean) : []; }
  _write(arr: string[]): void { this._el._className = arr.join(" "); }
  add(...cls: string[]): void { const t = this._tokens(); for (const c of cls) if (!t.includes(c)) t.push(c); this._write(t); }
  remove(...cls: string[]): void { this._write(this._tokens().filter((c) => !cls.includes(c))); }
  contains(c: string): boolean { return this._tokens().includes(c); }
  toggle(c: string): void { this.contains(c) ? this.remove(c) : this.add(c); }
  get length(): number { return this._tokens().length; }
  toString(): string { return this._el._className || ""; }
}

// A CSSOM-ish style object: the renderer only writes scalar string CSS props
// (e.g. el.style.textAlign). We record props but never mirror them to a `style`
// attribute (the F4 story keeps CSSOM writes OFF the attribute).
type Style = Record<string, string>;

// --- a minimal CSSOM-ish style object (records props; reflects to attribute) --
function makeStyle(): Style {
  // The renderer only ever writes el.style.textAlign (a CSSOM write the F4 story
  // keeps OFF the style attribute). We record props but do NOT mirror them into a
  // `style` attribute, matching browser CSSOM behaviour closely enough for the
  // oracle's "no style attribute" check.
  return {};
}

// --- shared parent-node mixin (Element + DocumentFragment) --------------------
class ParentNode {
  childNodes: DomNode[];
  parentNode: ParentNode | null;
  constructor() {
    this.childNodes = [];
    this.parentNode = null;
  }
  appendChild<T extends DomNode>(node: T | null): T | null {
    if (node == null) return node;
    if (node.nodeType === DOCUMENT_FRAGMENT_NODE) {
      // Browser semantics: appending a fragment MOVES its children and empties it.
      const frag = node as DocumentFragment; // narrowed by nodeType above
      const kids = frag.childNodes.slice();
      frag.childNodes.length = 0;
      for (const k of kids) { k.parentNode = this; this.childNodes.push(k); }
      return node;
    }
    if (node.parentNode) {
      const i = node.parentNode.childNodes.indexOf(node);
      if (i >= 0) node.parentNode.childNodes.splice(i, 1);
    }
    node.parentNode = this;
    this.childNodes.push(node);
    return node;
  }
  append(...nodes: (DomNode | string)[]): void {
    for (const n of nodes) {
      if (typeof n === "string") this.appendChild(new TextNode(n));
      else this.appendChild(n);
    }
  }
  get firstChild(): DomNode | null { return this.childNodes[0] || null; }
  get childElementCount(): number { return this.childNodes.filter((n) => n.nodeType === ELEMENT_NODE).length; }

  get textContent(): string {
    let s = "";
    const stack: DomNode[] = this.childNodes.slice().reverse();
    while (stack.length) {
      const n = stack.pop()!; // invariant: stack.length > 0 in this loop
      if (n.nodeType === TEXT_NODE) s += (n as TextNode).data;
      else { const kids = (n as ParentNode).childNodes; for (let i = kids.length - 1; i >= 0; i--) stack.push(kids[i]!); }
    }
    return s;
  }

  querySelectorAll(sel: string): Element[] { const out: Element[] = []; collectMatches(this, sel, out); return out; }
  querySelector(sel: string): Element | null { return this.querySelectorAll(sel)[0] || null; }
}

// --- element -----------------------------------------------------------------
class Element extends ParentNode {
  nodeType: number;
  nodeName: string;
  namespaceURI: string | null;
  localName: string;
  tagName: string;
  _attrs: Record<string, string>;
  _className: string;
  _classList: ClassList;
  style: Style;
  dataset: Record<string, string>;
  constructor(localName: unknown, namespaceURI?: string | null) {
    super();
    this.nodeType = ELEMENT_NODE;
    this.namespaceURI = namespaceURI || null;
    this.localName = String(localName);
    this.tagName = namespaceURI ? this.localName : this.localName.toUpperCase();
    this.nodeName = this.tagName;
    this.parentNode = null;
    this._attrs = Object.create(null); // name -> value (string)
    this._className = "";
    this._classList = new ClassList(this);
    this.style = makeStyle();
    this.dataset = Object.create(null);
  }

  setAttribute(name: string, value: unknown): void {
    const v = String(value);
    this._attrs[name] = v;
    if (name === "class") this._className = v;
    if (name.startsWith("data-")) this.dataset[dashToCamel(name.slice(5))] = v;
  }
  getAttribute(name: string): string | null {
    if (name === "class") return this._className || (name in this._attrs ? this._attrs[name]! : null);
    return name in this._attrs ? this._attrs[name]! : null;
  }
  hasAttribute(name: string): boolean { return name === "class" ? !!this._className : name in this._attrs; }
  removeAttribute(name: string): void { delete this._attrs[name]; if (name === "class") this._className = ""; }

  get attributes(): { name: string; value: string }[] {
    const list: { name: string; value: string }[] = [];
    for (const k in this._attrs) list.push({ name: k, value: this._attrs[k]! });
    if (this._className && !("class" in this._attrs)) list.push({ name: "class", value: this._className });
    return list;
  }

  get className(): string { return this._className; }
  set className(v: unknown) { this._className = String(v); this._attrs.class = this._className; }
  get classList(): ClassList { return this._classList; }

  set textContent(v: unknown) {
    this.childNodes.length = 0;
    if (v !== "" && v != null) this.appendChild(new TextNode(v));
  }
  get textContent(): string { return super.textContent; }

  get children(): Element[] { return this.childNodes.filter((n): n is Element => n.nodeType === ELEMENT_NODE); }
  get id(): string { return this.getAttribute("id") || ""; }
  set id(v: unknown) { this.setAttribute("id", v); }
}

function dashToCamel(s: string): string { return s.replace(/-([a-z])/g, (_, c: string) => c.toUpperCase()); }

// --- document fragment -------------------------------------------------------
class DocumentFragment extends ParentNode {
  nodeType: number;
  nodeName: string;
  constructor() {
    super();
    this.nodeType = DOCUMENT_FRAGMENT_NODE;
    this.nodeName = "#document-fragment";
    this.parentNode = null;
  }
  set textContent(v: unknown) { this.childNodes.length = 0; if (v) this.appendChild(new TextNode(v)); }
  get textContent(): string { return super.textContent; }
}

// --- a small CSS-selector subset (enough for the renderer) -------------------
// Supports comma lists; descendant combinator (whitespace); each simple selector
// is [tag][.class...][#id][\[attr\]][\[attr=val\]] and the escaped pseudo
// "[xlink\:href]" the gallery uses. Matching is over DESCENDANTS (querySelectorAll
// semantics). assignHeadingIds only needs the "h1,h2,..,h6" comma-of-tags form.
interface SimpleSelector {
  tag: string | null;
  classes: string[];
  id: string | null;
  attrs: { name: string; val: string | undefined }[];
}
function parseSimple(sel: string): SimpleSelector {
  const parts: SimpleSelector = { tag: null, classes: [], id: null, attrs: [] };
  let s = sel.trim();
  // attribute selectors first (may contain escaped colon: [xlink\:href])
  s = s.replace(/\[([^\]]+)\]/g, (_, body: string) => {
    const eq = body.indexOf("=");
    if (eq >= 0) {
      let name = body.slice(0, eq).trim();
      let val = body.slice(eq + 1).trim().replace(/^["']|["']$/g, "");
      parts.attrs.push({ name: name.replace(/\\/g, ""), val });
    } else {
      parts.attrs.push({ name: body.trim().replace(/\\/g, ""), val: undefined });
    }
    return "";
  });
  // id
  s = s.replace(/#([\w-]+)/g, (_, id: string) => { parts.id = id; return ""; });
  // classes
  s = s.replace(/\.([\w-]+)/g, (_, c: string) => { parts.classes.push(c); return ""; });
  // remaining is the tag (or empty / '*')
  const tag = s.trim();
  if (tag && tag !== "*") parts.tag = tag.toLowerCase();
  return parts;
}

function matchSimple(el: DomNode, simple: SimpleSelector): boolean {
  if (el.nodeType !== ELEMENT_NODE) return false;
  const elem = el as Element; // narrowed by nodeType above
  if (simple.tag && elem.localName.toLowerCase() !== simple.tag) return false;
  if (simple.id && elem.getAttribute("id") !== simple.id) return false;
  for (const c of simple.classes) if (!elem.classList.contains(c)) return false;
  for (const a of simple.attrs) {
    if (!elem.hasAttribute(a.name)) return false;
    if (a.val !== undefined && elem.getAttribute(a.name) !== a.val) return false;
  }
  return true;
}

// match a descendant-combinator chain ending at `el`
function matchChain(el: Element, simples: SimpleSelector[]): boolean {
  let i = simples.length - 1;
  if (!matchSimple(el, simples[i]!)) return false;
  i--;
  let anc: ParentNode | null = el.parentNode;
  while (i >= 0) {
    let ok = false;
    while (anc) { if (matchSimple(anc as DomNode, simples[i]!)) { ok = true; anc = anc.parentNode; break; } anc = anc.parentNode; }
    if (!ok) return false;
    i--;
  }
  return true;
}

function collectMatches(root: ParentNode, selector: string, out: Element[]): void {
  const groups = String(selector).split(",").map((g) => g.trim()).filter(Boolean)
    .map((g) => g.split(/\s+/).filter(Boolean).map(parseSimple));
  const stack: DomNode[] = root.childNodes.slice().reverse();
  while (stack.length) {
    const n = stack.pop()!; // invariant: stack.length > 0 in this loop
    if (n.nodeType === ELEMENT_NODE) {
      const elem = n as Element; // narrowed by nodeType above
      for (const simples of groups) {
        if (matchChain(elem, simples)) { out.push(elem); break; }
      }
    }
    const kids = (n as ParentNode).childNodes;
    if (kids) for (let i = kids.length - 1; i >= 0; i--) stack.push(kids[i]!);
  }
}

// --- document ----------------------------------------------------------------
class Document {
  adoptedStyleSheets: CSSStyleSheet[];
  nodeType: number;
  constructor() {
    this.adoptedStyleSheets = [];
    this.nodeType = 9;
  }
  createElement(tag: string): Element { return new Element(tag, null); }
  createElementNS(ns: string, tag: string): Element { return new Element(tag, ns); }
  createTextNode(data: unknown): TextNode { return new TextNode(data); }
  createDocumentFragment(): DocumentFragment { return new DocumentFragment(); }
}

class CSSStyleSheet {
  cssText: string;
  constructor() { this.cssText = ""; }
  replaceSync(css: unknown): void { this.cssText = String(css); }
  replace(css: unknown): Promise<this> { this.cssText = String(css); return Promise.resolve(this); }
}

// The slice of the global object installDom() writes to. Typed locally (lib is
// ES2022, so globalThis has no DOM) - readers narrow globalThis.document elsewhere.
interface DomGlobal {
  document?: Document;
  CSSStyleSheet?: typeof CSSStyleSheet;
  Node?: typeof Node;
  __gertDomShim?: boolean;
}

// install(globalThis) wires the globals the renderer expects, idempotently.
export function installDom(target: DomGlobal = globalThis as unknown as DomGlobal): Document {
  if (target.__gertDomShim) return target.document!; // set on first install (below)
  const doc = new Document();
  target.document = doc;
  target.CSSStyleSheet = CSSStyleSheet;
  target.Node = Node;
  target.__gertDomShim = true;
  return doc;
}

export { Element, TextNode, DocumentFragment, Document, CSSStyleSheet };
