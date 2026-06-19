// Fenced-code leaf of the markdown renderer as a VanJS component: the structural
// renderer calls MdCode for every code_block node and inserts the returned DOM
// verbatim. Wraps lib/highlight.js into <pre data-lang><code>...</code></pre>
// where <code> holds ONLY inert tok-* spans + text nodes built from textContent,
// never an HTML string (HARD CONTRACT 2). data-lang is model-controlled, so it is
// guarded to a short identifier slice and only ever lands in dataset, never parsed
// as HTML. view() uses document.createElement (NOT van.tags, which has no
// allow-list) so F4 holds via the closed builders. The emitted DOM shape stays
// byte-identical to the old inline renderCodeBlock output, so message.js
// querySelectorAll("pre"), the .tok-* CSS, and the byte-oracle still hold.
import { component } from "../../../lib/component.js";
import { highlight } from "../../../lib/highlight.js";

const LANG_RX = /^[\w+#.-]{1,16}$/;

export const MdCode = component({
  name: "md-code",
  css: `
    /* Fenced-code token tints: colors resolve per theme via the --tok-* tokens
       so code follows the theme like everything else. */
    .tok-kw {
      color: var(--tok-kw);
    }
    .tok-str {
      color: var(--tok-str);
    }
    .tok-num {
      color: var(--tok-num);
    }
    .tok-com {
      color: var(--tok-com);
      font-style: italic;
    }
    .tok-key {
      color: var(--tok-key);
    }
    .tok-dec {
      color: var(--tok-dec);
    }
  `,
  // code/lang are model-derived; both are coerced/guarded below before any DOM use.
  view: ({ code, lang }: { code?: unknown; lang?: string | null | undefined }) => {
    const pre = document.createElement("pre");
    const codeEl = document.createElement("code");
    const text = String(code ?? "");
    for (const n of highlight(text, lang)) codeEl.appendChild(n);
    // LANG_RX is /^[\w+#.-]{1,16}$/ (min length 1), so a match on (lang || "")
    // can only succeed for a non-empty string `lang` - never "" or null/undefined.
    if (LANG_RX.test(lang || "")) pre.dataset.lang = lang!.toLowerCase();
    pre.appendChild(codeEl);
    return pre;
  },
});

export default MdCode;
