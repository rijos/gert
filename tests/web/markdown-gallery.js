// markdown-gallery.js - the markdown renderer gallery bootstrap (testing.md
// section 8). EXTERNAL module (served from tests/web/ at /tests/) because the
// host CSP is `script-src 'self'` plus one import-map hash, so an inline script
// would be blocked.
//
// It imports the REAL renderer on the same origin and renders a labeled battery
// of CommonMark/GFM inputs plus a security panel. Each rendered card is built
// from renderMarkdown's DocumentFragment (real DOM nodes), so what you see is
// exactly what the chat + canvas viewers show. The security panel asserts F4:
// raw HTML/<script> renders as literal text and dangerous URLs collapse to "#".
import { renderMarkdown, sanitizeUrl } from "/lib/markdown.js";

const root = document.getElementById("gallery-root");

const FEATURES = [
  ["nested emphasis", "**bold with *nested* italic** and ***both***"],
  ["intraword underscore", "snake_case_variable_name stays plain; 2 * 3 * 4 = 24"],
  ["code spans (var backticks)", "Use `` a`b `` and ``5`` and `git commit`."],
  ["backslash escapes", "Literal \\*asterisks\\*, \\`backticks\\`, and \\[brackets\\]."],
  ["strikethrough", "Done ~~old text~~ now; **~~bold strike~~**."],
  ["images", "![diagram](https://example.com/d.png)"],
  ["autolinks", "See <http://example.com>, https://en.wikipedia.org/wiki/Foo_bar, and bob@example.com."],
  ["links (parens / brackets / title)", '[the guide](http://h/(v1)/start) and [a [b] c](http://x "Title")'],
  ["hard line break", "First line  \nSecond line\\\nThird line"],
  ["entities", "AT&T &amp; &#39;quotes&#39; &copy; &mdash; done"],
  ["ATX + setext headings", "# ATX H1\n\nSetext H1\n=========\n\nSetext H2\n---------"],
  ["thematic break", "Section one.\n\n---\n\nSection two."],
  ["ordered list start", "3. Third\n4. Fourth\n5. Fifth"],
  ["nested + lazy list", "- Parent\n  - Child a\n  - Child b that wraps\n    onto two lines\n- Parent two"],
  ["mixed nested list", "1. Step one\n   - sub a\n   - sub b\n2. Step two"],
  ["task list", "- [ ] write tests\n- [x] **fix** bug\n- [ ] ship"],
  ["blockquote (recursive)", "> # Quoted heading\n>\n> Body of the quote.\n>\n> - a bullet\n> - another\n>\n> > nested quote"],
  ["fenced + indented code", "```python\ndef f(x):\n    return x + 1\n```\n\n    plain indented code"],
  ["GFM table", "| Feature | Status | Owner |\n| :--- | :---: | ---: |\n| `a|b` cell | **done** | ~~old~~ |\n| Login | WIP | Ana |"],
  ["ragged table rows", "| a | b | c |\n| --- | --- | --- |\n| 1 | 2 | 3 | 4 |\n| only one |"],
  ["citation markers survive", "The result [1] cites a source [2] - injectCitations finds these."],
  ["heading anchors + in-doc link", "[jump to setup](#setup)\n\n## Setup\n\nBody.\n\n## Setup\n\nA second same-named heading."],
  ["go highlight", "```go\npackage main\n\nimport \"fmt\"\n\nfunc main() {\n  s := `raw\\nstring`\n  fmt.Println(\"hi\", len(s), 0xFF) // comment\n}\n```"],
  ["bash highlight", "```bash\n#!/usr/bin/env bash\nfor f in *.txt; do\n  echo \"${f#prefix}\" \"$HOME\" \"$1\"\ndone\n```"],
  ["inline + display math (smath -> MathML)", "Euler's identity $e^{i\\pi} + 1 = 0$, and a display sum:\n\n$$\\sum_{k=1}^{n} k = \\frac{n(n+1)}{2}$$"],
  ["math vs currency", "It costs $5 today, $10 tomorrow (not math), but $a^2 + b^2 = c^2$ is."],
  ["LaTeX \\(..\\) / \\[..\\] delimiters", "Inline \\(e^{i\\pi} + 1 = 0\\), and a display block:\n\n\\[ \\int_0^\\infty e^{-x^2}\\,dx = \\frac{\\sqrt{\\pi}}{2} \\]"],
];

// Each: [label, source, fn(host) -> {ok, detail}]. Functional (non-security)
// self-checks - same shape as SECURITY below, folded into the page verdict.
const FUNCTIONAL = [
  ["heading gets a slug id", "## Getting Started", (h) =>
    pass(h.querySelector("h2")?.id === "getting-started", "h2#getting-started")],
  ["in-doc link resolves to its heading", "[go](#setup)\n\n## Setup", (h) => {
    const href = h.querySelector("a")?.getAttribute("href");
    const target = h.querySelector("h2")?.id;
    return pass(href === "#setup" && target === "setup", `${href} -> #${target}`);
  }],
  ["duplicate headings get -1 suffix", "## Setup\n\n## Setup", (h) => {
    const ids = [...h.querySelectorAll("h2")].map((x) => x.id);
    return pass(ids[0] === "setup" && ids[1] === "setup-1", ids.join(", "));
  }],
  ["heading id is inert (punctuation stripped)", "## C# & <b>x</b>!", (h) =>
    pass(/^[\w-]*$/.test(h.querySelector("h2")?.id || ""), `id="${h.querySelector("h2")?.id}"`)],
  ["go fence tints + carries data-lang", "```go\nfunc f() {}\n```", (h) => {
    const pre = h.querySelector("pre");
    return pass(pre?.dataset.lang === "go" && pre.querySelector(".tok-kw"), "data-lang=go, has tok-kw");
  }],
  ["bash/shell alias tints", "```shell\nexport X=1\n```", (h) =>
    pass(h.querySelector("pre .tok-kw") != null, "shell -> bash tokens")],
  ["inline math builds <math>", "$x^2 + 1$", (h) =>
    pass(h.querySelector(".md-math math") != null, "smath rendered inline <math>")],
  ["display math builds a block", "$$\\int_0^1 x\\,dx$$", (h) =>
    pass(h.querySelector(".md-math-block math") != null, "block <math> in .md-math-block")],
  ["currency is not math", "I have $5 and $10 left", (h) =>
    pass(h.querySelector("math") == null && h.textContent.includes("$5"), "no <math>, $5/$10 literal")],
  ["invalid math renders inert (no throw)", "$\\frac{a}{$", (h) =>
    pass(h.querySelector(".md-math") != null, "incomplete TeX -> inert .md-math node, no crash")],
  ["inline \\(..\\) renders math", "\\(a^2 + b^2\\)", (h) =>
    pass(h.querySelector(".md-math math") != null, "\\(..\\) -> inline <math>")],
  ["display \\[..\\] renders block", "\\[ x = y \\]", (h) =>
    pass(h.querySelector(".md-math-block math") != null, "\\[..\\] -> block <math>")],
  ["mid-line \\[..\\] stays a literal escape", "see \\[not math\\] here", (h) =>
    pass(h.querySelector("math") == null && h.textContent.includes("[not math]"), "inline \\[ is a bracket escape, not display math")],
  // --- Goal B: the 4 documented edge cases, pinned to the stricter single form.
  // classifyLine() runs ONE LINE_KINDS test per kind and feeds BOTH dispatch and
  // the paragraph-interrupt, so each of these resolves to a single CommonMark-
  // stricter answer (the old looser break-check can no longer pick a 2nd reading).
  ["edge: bare '######' is an empty ATX heading (stricter)", "######", (h) => {
    const head = h.querySelector("h6");
    return pass(head != null && head.textContent === "" && h.querySelector("p") == null,
      "######  ->  <h6></h6> (ATX wins, empty content)");
  }],
  ["edge: prefix-only '$$' opens a display-math block (stricter)", "$$\n\\sum x\n$$", (h) =>
    pass(h.querySelector(".md-math-block math") != null && h.querySelector("p") == null,
      "lone $$ line opens a math block, not a paragraph")],
  ["edge: mid-line '\\[' is NOT display math (stricter)", "see \\[not math\\] here", (h) =>
    pass(h.querySelector("math") == null && h.querySelector("p") != null && h.textContent.includes("[not math]"),
      "\\[ only opens math at line start; mid-line stays a paragraph escape")],
  ["edge: over-indented table-shaped line is indented code (stricter)", "     | a | b |\n     | - | - |", (h) => {
    const code = h.querySelector("pre code");
    return pass(code != null && h.querySelector("table") == null && code.textContent.includes("| a | b |"),
      "5-space indent wins -> indented code, not a GFM table");
  }],
  // Regression pin (review-found 5th edge): a MULTI-line paragraph whose last line
  // is followed by a setext underline must stay a paragraph + <hr>, NOT split with
  // its last line eaten into a heading. Setext forms only at dispatch from a first
  // line; an interior paragraph line is never reclassified by its lookahead.
  ["multi-line paragraph before '---' stays a paragraph (no setext split)", "line one\nline two\n---", (h) => {
    const p = h.querySelector("p");
    return pass(p != null && p.textContent.includes("line one") && p.textContent.includes("line two") &&
      h.querySelector("hr") != null && h.querySelector("h1, h2") == null,
      "para keeps both lines + <hr>; trailing --- never becomes a heading");
  }],
];

// Each: [label, source, fn(host) -> {ok, detail}]. The check reads back the DOM
// the renderer produced, the same way the Playwright component test does.
const SECURITY = [
  ["raw <script> is literal text", "<script>alert(document.cookie)</script>", (h) =>
    pass(h.querySelectorAll("script").length === 0 && h.textContent.includes("<script>"), "no live <script>")],
  ["raw <img onerror> is literal text", "Click <img src=x onerror=alert(1)> here", (h) =>
    pass(h.querySelectorAll("img").length === 0, "no <img> node built from raw HTML")],
  ["javascript: link -> #", "[click](javascript:alert(document.domain))", (h) =>
    pass(h.querySelector("a").getAttribute("href") === "#", "href neutralized")],
  ["mixed-case / vbscript / data:text/html -> #", "[a](JaVaScRiPt:x) [b](VBScript:y) [c](data:text/html,z)", (h) =>
    pass([...h.querySelectorAll("a")].every((a) => a.getAttribute("href") === "#"), "all three neutralized")],
  ["entity-encoded colon scheme -> #", "[x](javascript&colon;alert(1))", (h) =>
    pass(h.querySelector("a").getAttribute("href") === "#", "&colon; folded then rejected")],
  ["control-char smuggled scheme -> #", "[x](<java\tscript:alert(1)>)", (h) =>
    pass(h.querySelector("a").getAttribute("href") === "#", "tab-smuggled scheme rejected")],
  ["attribute-injection href stays inert", '[x](http://h/?q=a" onmouseover="alert(1))', (h) =>
    pass(h.querySelectorAll("[onmouseover]").length === 0, "no onmouseover attribute forged")],
  ["external link gets rel+target", "[home](//evil.example.com/phish)", (h) => {
    const a = h.querySelector("a");
    return pass(a.getAttribute("rel") === "noopener noreferrer" && a.getAttribute("target") === "_blank", "rel/target set");
  }],
  ["image src scrubbed (javascript:)", "![x](javascript:alert(1))", (h) =>
    pass(h.querySelector("img").getAttribute("src") === "#", "img src neutralized")],
  ["image data:image/png allowed", "![ok](data:image/png;base64,iVBOR)", (h) =>
    pass(h.querySelector("img").getAttribute("src").startsWith("data:image/png"), "safe image data: kept")],
  ["image data:image/svg+xml rejected", "![bad](data:image/svg+xml;base64,PHN2Zz4=)", (h) =>
    pass(h.querySelector("img").getAttribute("src") === "#", "scriptable svg data: rejected")],
  ["math \\href is untrusted (no link)", "$\\href{javascript:alert(1)}{x}$", (h) =>
    pass(h.querySelectorAll("a").length === 0, "trust:false -> \\href makes no <a>")],
  ["raw HTML inside math is inert", "$\\text{<script>alert(1)</script>}$", (h) =>
    pass(h.querySelectorAll("script").length === 0, "no live <script> from math text")],
  ["math output carries no style attribute", "$\\pmb{x}$", (h) =>
    pass(h.querySelectorAll(".md-math [style]").length === 0, "\\pmb style moved to CSSOM (CSP-safe)")],
  ["no inline-HTML sink attr lands on math output", "$\\href{javascript:alert(1)}{x}\\src{x}{onerror}$", (h) => {
    const sink = h.querySelectorAll(".md-math [href], .md-math [src], .md-math [onerror], .md-math [xlink\\:href]");
    return pass(sink.length === 0 && h.querySelectorAll(".md-math a, .md-math img").length === 0,
      "MathML allow-list drops href/src/onerror; no <a>/<img> forged from TeX");
  }],
  ["cross-origin image neutralized", "![pixel](https://picsum.photos/seed/a/40/40)", (h) =>
    pass(h.querySelector("img").getAttribute("src") === "#", "foreign-origin img -> # (no doomed fetch)")],
  ["protocol-relative image neutralized", "![x](//evil.example/p.png)", (h) =>
    pass(h.querySelector("img").getAttribute("src") === "#", "//host img -> #")],
  ["same-origin/relative image neutralized", "![a](/c/assets/diagram.png) ![b](diagram.png)", (h) =>
    pass([...h.querySelectorAll("img")].every((x) => x.getAttribute("src") === "#"), "no url-shaped img fetches (only data:image)")],
];

function pass(ok, detail) { return { ok, detail }; }

function buildCase(name, src, sec) {
  const card = document.createElement("div");
  card.className = sec ? "case sec" : "case";
  const head = document.createElement("div");
  head.className = "name";
  head.textContent = name;
  const cols = document.createElement("div");
  cols.className = "cols";
  const srcPre = document.createElement("pre");
  srcPre.className = "src";
  srcPre.textContent = src;
  const out = document.createElement("div");
  out.className = "out";
  out.append(renderMarkdown(src)); // the real renderer, real DOM nodes
  cols.append(srcPre, out);
  card.append(head, cols);
  return { card, out };
}

// feature gallery
const features = document.createElement("section");
const ftitle = document.createElement("h1");
ftitle.textContent = "lib/markdown.js gallery";
const fsub = document.createElement("p");
fsub.className = "sub";
fsub.textContent = "Real renderer, same origin, DOM nodes only (security F4). Left: source. Right: live render.";
features.append(ftitle, fsub);
for (const [name, src] of FEATURES) features.append(buildCase(name, src, false).card);
root.append(features);

// security panel (each card carries an auto-verdict so the page self-checks)
const security = document.createElement("section");
const stitle = document.createElement("h1");
stitle.textContent = "Security (F4) self-checks";
security.append(stitle);
let allOk = true;
for (const [name, src, check] of SECURITY) {
  const { card, out } = buildCase(name, src, true);
  const { ok, detail } = check(out);
  allOk = allOk && ok;
  const verdict = document.createElement("div");
  verdict.className = "verdict " + (ok ? "pass" : "fail");
  verdict.textContent = (ok ? "PASS - " : "FAIL - ") + detail;
  card.append(verdict);
  security.append(card);
}
root.append(security);

// functional panel (anchors, highlight languages) - same self-check shape.
const functional = document.createElement("section");
const ftitle2 = document.createElement("h1");
ftitle2.textContent = "Functional self-checks";
functional.append(ftitle2);
let funcOk = true;
for (const [name, src, check] of FUNCTIONAL) {
  const { card, out } = buildCase(name, src, true);
  const { ok, detail } = check(out);
  funcOk = funcOk && ok;
  const verdict = document.createElement("div");
  verdict.className = "verdict " + (ok ? "pass" : "fail");
  verdict.textContent = (ok ? "PASS - " : "FAIL - ") + detail;
  card.append(verdict);
  functional.append(card);
}
root.append(functional);

// A machine-readable summary for Playwright: window.__galleryResult = {...}.
// Also spot-checks sanitizeUrl directly (exported, used by callers elsewhere).
window.__galleryResult = {
  featureCount: FEATURES.length,
  securityCount: SECURITY.length,
  securityAllPass: allOk,
  functionalCount: FUNCTIONAL.length,
  functionalAllPass: funcOk,
  // a couple of direct sanitizeUrl probes for completeness
  sanitize: {
    js: sanitizeUrl("javascript:alert(1)"),
    jsSpaced: sanitizeUrl(" javascript:x"),
    jsTab: sanitizeUrl("java\tscript:1"),
    httpOk: sanitizeUrl("http://ok/path"),
    rel: sanitizeUrl("/relative"),
    anchor: sanitizeUrl("#a"),
    protoRel: sanitizeUrl("//host"),
  },
};
window.__galleryReady = true;
