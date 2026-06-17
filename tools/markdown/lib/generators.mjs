// generators.mjs - grammar-aware random markdown generators. Pure random bytes
// mostly produce paragraphs; a grammar that knows the renderer's constructs digs
// far deeper. Every generator takes an Rng (lib/rng.mjs) so output is a pure
// function of the seed (reproducible). An `evil` channel injects the shapes that
// stress the renderer's bounds/totality/regex: delimiter walls, near-cap nesting,
// unbalanced fences/brackets, currency-vs-math ambiguity, and ReDoS-bait code
// bodies for the highlight lexer.

const WORDS = ["the", "quick", "brown", "fox", "lorem", "ipsum", "dolor", "a", "I", "x", "C#", "AT&T", "2+2", "foo_bar", "n", "k"];
const PUNCT = ["!", "?", ".", ",", ";", ":", "-", "(", ")", "<", ">", "&", "|", "/", "\\", "*", "_", "~", "`", "$", "[", "]", "#", "=", "+"];
const LANGS = ["python", "javascript", "go", "rust", "cpp", "csharp", "bash", "json", "xml", "markdown", "", "unknownlang", "c#", "c++"];
const URLS = ["https://example.com/a", "http://h/(v1)/x", "/rel/path", "#anchor", "mailto:a@b.com",
  "javascript:alert(1)", "JaVaScRiPt:x", "data:text/html,z", "data:image/png;base64,AAAA",
  "data:image/svg+xml,PHN2Zz4=", "//evil.example", 'http://h/?q="onx="y'];
const TEX = ["e^{i\\pi}+1=0", "\\frac{a}{b}", "\\sum_{k=1}^n k", "\\sqrt[3]{x}", "x^2", "\\begin{pmatrix}a&b\\\\c&d\\end{pmatrix}",
  "\\href{javascript:alert(1)}{x}", "\\text{<script>x</script>}", "{{{{", "\\frac\\frac\\frac", "\\not="];

// ---- inline fragments -------------------------------------------------------
function word(rng) { return rng.pick(WORDS); }
function runOf(rng, ch, max = 8) { return ch.repeat(rng.range(1, max)); }

function inlineFrag(rng, depth) {
  const evil = rng.chance(0.22);
  const kind = rng.weighted([
    ["text", 5], ["emph", 3], ["code", 2], ["link", 2], ["image", 1],
    ["autolink", 1], ["math", 2], ["entity", 1], ["escape", 1], ["delimwall", evil ? 3 : 0.3],
    ["raw", 1], ["break", 0.5],
  ]);
  switch (kind) {
    case "text": return rng.times(rng.range(1, 4), () => word(rng)).join(" ");
    case "emph": {
      const d = rng.pick(["*", "_", "~~", "**", "***", "__"]);
      const inner = depth > 0 ? inlineFrag(rng, depth - 1) : word(rng);
      return rng.chance(0.85) ? d + inner + d : d + inner; // sometimes unbalanced
    }
    case "code": { const t = runOf(rng, "`", 4); return t + " " + word(rng) + (rng.chance(0.8) ? " " + t : ""); }
    case "link": { const txt = rng.chance(0.5) ? inlineFrag(rng, 0) : word(rng); const u = rng.pick(URLS); const title = rng.chance(0.3) ? ' "t"' : ""; return `[${txt}](${u}${title})`; }
    case "image": return `![${word(rng)}](${rng.pick(URLS)})`;
    case "autolink": return rng.pick(["<http://x.com>", "https://en.wikipedia.org/wiki/Foo_(bar)", "bob@example.com", "<not a url>", "www.example.com"]);
    case "math": { const t = rng.pick(TEX); const m = rng.pick(["$", "$$", "\\(", "\\["]); const close = m === "\\(" ? "\\)" : m === "\\[" ? "\\]" : m; return rng.chance(0.85) ? m + t + close : m + t; }
    case "entity": return rng.pick(["&amp;", "&#39;", "&#x1F600;", "&copy;", "&notreal;", "&#xZZ;", "&#999999999;"]);
    case "escape": return "\\" + rng.pick(PUNCT);
    case "delimwall": return runOf(rng, rng.pick(["*", "_", "~", "`", "$", "[", "]", "(", ")"]), rng.range(8, 60));
    case "raw": return rng.pick(["<script>alert(1)</script>", "<img src=x onerror=alert(1)>", "<b>x</b>", "<div onclick=y>", "</p>", "<!-- c -->"]);
    case "break": return rng.chance(0.5) ? "  \n" : "\\\n";
    default: return word(rng);
  }
}

function inlineLine(rng, depth = 2) {
  const n = rng.range(1, 5);
  return rng.times(n, () => inlineFrag(rng, depth)).join(rng.chance(0.7) ? " " : "");
}

// ---- adversarial code bodies (highlight ReDoS bait) -------------------------
function evilCode(rng) {
  const big = rng.range(50, 1200);
  return rng.pick([
    '@"' + '"'.repeat(big),                       // C# verbatim string, quote wall
    '$$"' + '"'.repeat(big),                       // C# interpolated-verbatim
    'R"x(' + ")".repeat(big),                      // C++ raw string, no close
    'R"' + "(".repeat(big),                         // C++ raw, unbounded delim id
    'r' + "#".repeat(big) + '"x',                   // rust raw string hashes
    'br"' + "\\".repeat(big),                       // rust byte string escapes
    "/*" + "*".repeat(big),                         // unclosed block comment
    "'" + "\\".repeat(big),                         // char-literal escape wall
    '"' + "\\".repeat(big) + '"',                   // string escape wall
    "#".repeat(big),                                 // bash comment / cpp directive wall
    "`".repeat(big),                                  // go raw string / md fence-ish
    "0x" + "f".repeat(big),                          // long number literal
  ]);
}

// ---- block generators -------------------------------------------------------
function fence(rng) {
  const t = rng.pick(["```", "~~~", "````"]);
  const lang = rng.pick(LANGS);
  const body = rng.chance(0.4) ? evilCode(rng)
    : rng.times(rng.range(1, 4), () => rng.pick([
        "def f(x): return x+1", "const y = `a${b}`;", "func main(){ s := `r` }",
        'let s = r#"raw"#;', '@"verbatim ""esc"""', "#include <x>", '{ "k": 1 }',
        "<a href='x'>y</a>", "echo \"$HOME\" # c",
      ])).join("\n");
  const close = rng.chance(0.85) ? "\n" + t : ""; // sometimes unclosed (streaming)
  return t + lang + "\n" + body + close;
}

function table(rng) {
  const cols = rng.range(1, rng.chance(0.1) ? 30 : 5);
  const aligns = rng.times(cols, () => rng.pick([":---", ":--:", "---:", "---"]));
  const cell = () => rng.chance(0.3) ? "`a|b`" : rng.chance(0.3) ? inlineFrag(rng, 0) : word(rng);
  const header = "| " + rng.times(cols, cell).join(" | ") + " |";
  const delim = "| " + aligns.join(" | ") + " |";
  const rows = rng.times(rng.range(1, rng.chance(0.1) ? 20 : 4), () => {
    const n = rng.range(1, cols + 2); // ragged
    return "| " + rng.times(n, cell).join(" | ") + " |";
  });
  return [header, delim, ...rows].join("\n");
}

function list(rng, depth) {
  const ordered = rng.chance(0.5);
  const items = rng.range(1, 5);
  const startN = rng.chance(0.3) ? rng.range(1, 999) : 1;
  const lines = [];
  for (let i = 0; i < items; i++) {
    const marker = ordered ? `${startN + i}${rng.pick([".", ")"])}` : rng.pick(["-", "*", "+"]);
    const task = !ordered && rng.chance(0.3) ? `[${rng.pick([" ", "x", "X"])}] ` : "";
    lines.push(`${marker} ${task}${inlineLine(rng, 1)}`);
    if (depth > 0 && rng.chance(0.4)) {
      const sub = block(rng, depth - 1).split("\n").map((l) => "  " + l);
      lines.push(...sub);
    }
  }
  return lines.join("\n");
}

function blockquote(rng, depth) {
  const inner = depth > 0 ? block(rng, depth - 1) : inlineLine(rng, 1);
  const prefix = "> ".repeat(rng.range(1, 3));
  return inner.split("\n").map((l) => prefix + l).join("\n");
}

function heading(rng) {
  if (rng.chance(0.3)) { // setext
    return inlineLine(rng, 1) + "\n" + rng.pick(["===", "---", "=", "-----"]);
  }
  return runOf(rng, "#", 7) + (rng.chance(0.9) ? " " : "") + inlineLine(rng, 1) + (rng.chance(0.2) ? " " + runOf(rng, "#", 4) : "");
}

function mathBlock(rng) {
  const t = rng.pick(TEX);
  return rng.chance(0.5) ? `$$\n${t}\n$$` : `\\[\n${t}\n\\]`;
}

function nestWall(rng) {
  // near-cap and over-cap nesting to probe bounds/totality
  const ch = rng.pick([">", "- ", "* ", "+ "]);
  const n = rng.range(20, rng.chance(0.5) ? 40 : 120);
  return rng.times(n, (i) => ch.repeat(i + 1) + (ch === ">" ? "" : "") + "x").join("\n").slice(0, 4000);
}

export function block(rng, depth = 3) {
  const kind = rng.weighted([
    ["para", 4], ["heading", 2], ["list", 2], ["quote", 1.5], ["fence", 2],
    ["table", 1.5], ["math", 1], ["hr", 0.5], ["indentcode", 0.7], ["nestwall", 0.4], ["blank", 0.5],
  ]);
  switch (kind) {
    case "para": return rng.times(rng.range(1, 3), () => inlineLine(rng, 2)).join("\n");
    case "heading": return heading(rng);
    case "list": return list(rng, depth);
    case "quote": return blockquote(rng, depth);
    case "fence": return fence(rng);
    case "table": return table(rng);
    case "math": return mathBlock(rng);
    case "hr": return rng.pick(["---", "***", "___", "- - -", "*  *  *"]);
    case "indentcode": return rng.times(rng.range(1, 3), () => "    " + inlineLine(rng, 0)).join("\n");
    case "nestwall": return nestWall(rng);
    case "blank": return "";
    default: return inlineLine(rng, 2);
  }
}

// generate(rng, opts) -> a full random document string.
export function generate(rng, { maxBlocks = 8, depth = 3, maxLen = 8000 } = {}) {
  const n = rng.range(1, maxBlocks);
  const parts = rng.times(n, () => block(rng, depth));
  let doc = parts.join("\n\n");
  if (doc.length > maxLen) doc = doc.slice(0, maxLen);
  return doc;
}

// generateInline(rng) -> a single inline-heavy line (focuses the inline lexer).
export function generateInline(rng) { return inlineLine(rng, 4); }
