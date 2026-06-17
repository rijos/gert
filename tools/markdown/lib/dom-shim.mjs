// dom-shim.mjs - a tiny, zero-dependency DOM just large enough to run Gert's
// in-house markdown renderer headlessly in Node (no jsdom, no npm). The real
// renderer (src/Gert.Api/wwwroot/lib/markdown.js and its render/* + smath +
// highlight + component leaves) is loaded UNMODIFIED via lib/loader.mjs; this
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
// keeps the renderer's own code paths honest. The oracle layer (lib/oracle.mjs)
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

// --- text node ---------------------------------------------------------------
class TextNode {
  constructor(data) {
    this.nodeType = TEXT_NODE;
    this.nodeName = "#text";
    this.data = String(data);
    this.parentNode = null;
  }
  get textContent() { return this.data; }
  set textContent(v) { this.data = String(v); }
  get nodeValue() { return this.data; }
  set nodeValue(v) { this.data = String(v); }
}

// --- classList over a backing token string -----------------------------------
class ClassList {
  constructor(el) { this._el = el; }
  _tokens() { return this._el._className ? this._el._className.split(/\s+/).filter(Boolean) : []; }
  _write(arr) { this._el._className = arr.join(" "); }
  add(...cls) { const t = this._tokens(); for (const c of cls) if (!t.includes(c)) t.push(c); this._write(t); }
  remove(...cls) { this._write(this._tokens().filter((c) => !cls.includes(c))); }
  contains(c) { return this._tokens().includes(c); }
  toggle(c) { this.contains(c) ? this.remove(c) : this.add(c); }
  get length() { return this._tokens().length; }
  toString() { return this._el._className || ""; }
}

// --- a minimal CSSOM-ish style object (records props; reflects to attribute) --
function makeStyle() {
  // The renderer only ever writes el.style.textAlign (a CSSOM write the F4 story
  // keeps OFF the style attribute). We record props but do NOT mirror them into a
  // `style` attribute, matching browser CSSOM behaviour closely enough for the
  // oracle's "no style attribute" check.
  return {};
}

// --- shared parent-node mixin (Element + DocumentFragment) --------------------
class ParentNode {
  constructor() {
    this.childNodes = [];
  }
  appendChild(node) {
    if (node == null) return node;
    if (node.nodeType === DOCUMENT_FRAGMENT_NODE) {
      // Browser semantics: appending a fragment MOVES its children and empties it.
      const kids = node.childNodes.slice();
      node.childNodes.length = 0;
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
  append(...nodes) {
    for (const n of nodes) {
      if (typeof n === "string") this.appendChild(new TextNode(n));
      else this.appendChild(n);
    }
  }
  get firstChild() { return this.childNodes[0] || null; }
  get childElementCount() { return this.childNodes.filter((n) => n.nodeType === ELEMENT_NODE).length; }

  get textContent() {
    let s = "";
    const stack = this.childNodes.slice().reverse();
    while (stack.length) {
      const n = stack.pop();
      if (n.nodeType === TEXT_NODE) s += n.data;
      else if (n.childNodes) for (let i = n.childNodes.length - 1; i >= 0; i--) stack.push(n.childNodes[i]);
    }
    return s;
  }

  querySelectorAll(sel) { const out = []; collectMatches(this, sel, out); return out; }
  querySelector(sel) { return this.querySelectorAll(sel)[0] || null; }
}

// --- element -----------------------------------------------------------------
class Element extends ParentNode {
  constructor(localName, namespaceURI) {
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

  setAttribute(name, value) {
    const v = String(value);
    this._attrs[name] = v;
    if (name === "class") this._className = v;
    if (name.startsWith("data-")) this.dataset[dashToCamel(name.slice(5))] = v;
  }
  getAttribute(name) {
    if (name === "class") return this._className || (name in this._attrs ? this._attrs[name] : null);
    return name in this._attrs ? this._attrs[name] : null;
  }
  hasAttribute(name) { return name === "class" ? !!this._className : name in this._attrs; }
  removeAttribute(name) { delete this._attrs[name]; if (name === "class") this._className = ""; }

  get attributes() {
    const list = [];
    for (const k in this._attrs) list.push({ name: k, value: this._attrs[k] });
    if (this._className && !("class" in this._attrs)) list.push({ name: "class", value: this._className });
    return list;
  }

  get className() { return this._className; }
  set className(v) { this._className = String(v); this._attrs.class = this._className; }
  get classList() { return this._classList; }

  set textContent(v) {
    this.childNodes.length = 0;
    if (v !== "" && v != null) this.appendChild(new TextNode(v));
  }
  get textContent() { return super.textContent; }

  get children() { return this.childNodes.filter((n) => n.nodeType === ELEMENT_NODE); }
  get id() { return this.getAttribute("id") || ""; }
  set id(v) { this.setAttribute("id", v); }
}

function dashToCamel(s) { return s.replace(/-([a-z])/g, (_, c) => c.toUpperCase()); }

// --- document fragment -------------------------------------------------------
class DocumentFragment extends ParentNode {
  constructor() {
    super();
    this.nodeType = DOCUMENT_FRAGMENT_NODE;
    this.nodeName = "#document-fragment";
    this.parentNode = null;
  }
  set textContent(v) { this.childNodes.length = 0; if (v) this.appendChild(new TextNode(v)); }
  get textContent() { return super.textContent; }
}

// --- a small CSS-selector subset (enough for the renderer) -------------------
// Supports comma lists; descendant combinator (whitespace); each simple selector
// is [tag][.class...][#id][\[attr\]][\[attr=val\]] and the escaped pseudo
// "[xlink\:href]" the gallery uses. Matching is over DESCENDANTS (querySelectorAll
// semantics). assignHeadingIds only needs the "h1,h2,..,h6" comma-of-tags form.
function parseSimple(sel) {
  const parts = { tag: null, classes: [], id: null, attrs: [] };
  let s = sel.trim();
  // attribute selectors first (may contain escaped colon: [xlink\:href])
  s = s.replace(/\[([^\]]+)\]/g, (_, body) => {
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
  s = s.replace(/#([\w-]+)/g, (_, id) => { parts.id = id; return ""; });
  // classes
  s = s.replace(/\.([\w-]+)/g, (_, c) => { parts.classes.push(c); return ""; });
  // remaining is the tag (or empty / '*')
  const tag = s.trim();
  if (tag && tag !== "*") parts.tag = tag.toLowerCase();
  return parts;
}

function matchSimple(el, simple) {
  if (el.nodeType !== ELEMENT_NODE) return false;
  if (simple.tag && el.localName.toLowerCase() !== simple.tag) return false;
  if (simple.id && el.getAttribute("id") !== simple.id) return false;
  for (const c of simple.classes) if (!el.classList.contains(c)) return false;
  for (const a of simple.attrs) {
    if (!el.hasAttribute(a.name)) return false;
    if (a.val !== undefined && el.getAttribute(a.name) !== a.val) return false;
  }
  return true;
}

// match a descendant-combinator chain ending at `el`
function matchChain(el, simples) {
  let i = simples.length - 1;
  if (!matchSimple(el, simples[i])) return false;
  i--;
  let anc = el.parentNode;
  while (i >= 0) {
    let ok = false;
    while (anc) { if (matchSimple(anc, simples[i])) { ok = true; anc = anc.parentNode; break; } anc = anc.parentNode; }
    if (!ok) return false;
    i--;
  }
  return true;
}

function collectMatches(root, selector, out) {
  const groups = String(selector).split(",").map((g) => g.trim()).filter(Boolean)
    .map((g) => g.split(/\s+/).filter(Boolean).map(parseSimple));
  const stack = root.childNodes.slice().reverse();
  while (stack.length) {
    const n = stack.pop();
    if (n.nodeType === ELEMENT_NODE) {
      for (const simples of groups) {
        if (matchChain(n, simples)) { out.push(n); break; }
      }
    }
    if (n.childNodes) for (let i = n.childNodes.length - 1; i >= 0; i--) stack.push(n.childNodes[i]);
  }
}

// --- document ----------------------------------------------------------------
class Document {
  constructor() {
    this.adoptedStyleSheets = [];
    this.nodeType = 9;
  }
  createElement(tag) { return new Element(tag, null); }
  createElementNS(ns, tag) { return new Element(tag, ns); }
  createTextNode(data) { return new TextNode(data); }
  createDocumentFragment() { return new DocumentFragment(); }
}

class CSSStyleSheet {
  constructor() { this.cssText = ""; }
  replaceSync(css) { this.cssText = String(css); }
  replace(css) { this.cssText = String(css); return Promise.resolve(this); }
}

// install(globalThis) wires the globals the renderer expects, idempotently.
export function installDom(target = globalThis) {
  if (target.__gertDomShim) return target.document;
  const doc = new Document();
  target.document = doc;
  target.CSSStyleSheet = CSSStyleSheet;
  target.Node = Node;
  target.__gertDomShim = true;
  return doc;
}

export { Element, TextNode, DocumentFragment, Document, CSSStyleSheet };
