// components/canvas/artifacts/md-code.js - the fenced-code leaf of the markdown
// renderer as a VanJS component. The structural renderer (lib/render/dom.js,
// folded into lib/markdown.js) calls MdCode for every code_block node; the
// returned DOM is inserted verbatim.
//
// MdCode wraps lib/highlight.js: code + lang -> <pre data-lang><code>…</code></pre>
// where <code> holds ONLY inert tok-* spans + text nodes (highlight builds them
// from textContent - never an HTML string, no attribute but class, no class
// outside tok-*; HARD CONTRACT 2). The shape is IDENTICAL to what renderCodeBlock
// emitted inline before, so message.js querySelectorAll("pre"), the .tok-* CSS,
// the data-lang chrome label, and the byte-oracle all still hold.
//
// data-lang surfaces the fence language for chrome (message.js code-head label);
// model-controlled, kept to a short identifier-ish slice guarded by
// /^[\w+#.-]{1,16}$/, and only ever lands in dataset (textContent), never parsed
// as HTML.
//
// view() builds <pre>/<code> with document.createElement (NOT van.tags - van.tags
// has no allow-list) so the renderer's headless graph stays loader-resolvable and
// F4 holds via the closed builders. The code/token css moved here from
// styles/base.css (.tok-* tints); component() adopts it once via a Constructable
// Stylesheet, CSP-clean under style-src 'self'.
import { component } from "../../../lib/component.js";
import { highlight } from "../../../lib/highlight.js";

const LANG_RX = /^[\w+#.-]{1,16}$/;

export const MdCode = component({
  name: "md-code",
  css: `
    /* fenced-code token tints (lib/highlight.js). The colors resolve per theme
       via the --tok-* tokens, which carry an AA-tuned manila (light paper)
       palette and the ember (dark) palette - so code follows the theme like
       everything else. */
    .tok-kw{color:var(--tok-kw);}
    .tok-str{color:var(--tok-str);}
    .tok-num{color:var(--tok-num);}
    .tok-com{color:var(--tok-com); font-style:italic;}
    .tok-key{color:var(--tok-key);}
    .tok-dec{color:var(--tok-dec);}
  `,
  view: ({ code, lang }) => {
    const pre = document.createElement("pre");
    const codeEl = document.createElement("code");
    const text = String(code ?? "");
    for (const n of highlight(text, lang)) codeEl.appendChild(n);
    if (LANG_RX.test(lang || "")) pre.dataset.lang = lang.toLowerCase();
    pre.appendChild(codeEl);
    return pre;
  },
});

export default MdCode;
