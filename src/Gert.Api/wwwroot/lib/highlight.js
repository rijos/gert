// highlight.js - tiny regex tokenizer for fenced code blocks (no npm).
// Security F4 (same stance as markdown.js): produces real DOM nodes
// (<span class="tok-*">) from textContent, never an HTML string. Coverage is
// deliberately minimal - strings, comments, keywords, numbers - for the
// languages the assistant actually emits; anything else renders as plain text.

// Each rule: [sticky regex, token class]. Order matters (strings/comments
// before keywords so their contents aren't re-tokenized).
const RULES = {
  json: [
    [/"(?:[^"\\]|\\.)*"(?=\s*:)/y, "key"],
    [/"(?:[^"\\]|\\.)*"/y, "str"],
    [/-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/y, "num"],
    [/\b(?:true|false|null)\b/y, "kw"],
  ],
  python: [
    [/#.*/y, "com"],
    [/[rbfu]{0,2}(?:"""[\s\S]*?"""|'''[\s\S]*?''')/iy, "str"],
    [/[rbfu]{0,2}(?:"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*')/iy, "str"],
    [/@\w[\w.]*/y, "dec"],
    [
      /\b(?:def|class|return|if|elif|else|for|while|in|not|and|or|import|from|as|with|try|except|finally|raise|lambda|yield|pass|break|continue|global|nonlocal|assert|del|is|None|True|False|async|await)\b/y,
      "kw",
    ],
    [/\b\d[\d_]*(?:\.[\d_]+)?(?:[eE][+-]?\d+)?j?\b/y, "num"],
  ],
  javascript: [
    [/\/\/.*/y, "com"],
    [/\/\*[\s\S]*?\*\//y, "com"],
    [/`(?:[^`\\]|\\.)*`/y, "str"],
    [/"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'/y, "str"],
    [
      /\b(?:const|let|var|function|return|if|else|for|while|do|switch|case|default|break|continue|new|delete|typeof|instanceof|in|of|class|extends|super|this|import|export|from|try|catch|finally|throw|async|await|yield|true|false|null|undefined|void)\b/y,
      "kw",
    ],
    [/\b\d[\d_]*(?:\.[\d_]+)?(?:[eE][+-]?\d+)?n?\b/y, "num"],
  ],
  csharp: [
    [/\/\/.*/y, "com"],
    [/\/\*[\s\S]*?\*\//y, "com"],
    // verbatim / verbatim-interpolated first ("" escapes, may span lines),
    // then ordinary + interpolated (backslash escapes), then char.
    [/[@$]{2}"(?:[^"]|"")*"|@"(?:[^"]|"")*"/y, "str"],
    [/\$?"(?:[^"\\\n]|\\.)*"/y, "str"],
    [/'(?:[^'\\\n]|\\.)'/y, "str"],
    [
      /\b(?:abstract|as|async|await|base|bool|break|byte|case|catch|char|checked|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|finally|fixed|float|for|foreach|get|goto|if|implicit|in|init|int|interface|internal|is|lock|long|nameof|namespace|new|null|object|operator|out|override|params|partial|private|protected|public|readonly|record|ref|required|return|sbyte|sealed|set|short|sizeof|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|var|virtual|void|volatile|when|where|while|yield)\b/y,
      "kw",
    ],
    [/\b\d[\d_]*(?:\.[\d_]+)?(?:[eE][+-]?\d+)?[fFdDmMuUlL]*\b/y, "num"],
  ],
  cpp: [
    [/\/\/.*/y, "com"],
    [/\/\*[\s\S]*?\*\//y, "com"],
    // sticky+m: ^ only matches when the cursor sits at a line start, so this
    // fires for real directives and never on a stray mid-line '#'.
    [/^[ \t]*#[ \t]*\w+/my, "dec"],
    [/R"([^(\s]*)\(([\s\S]*?)\)\1"/y, "str"],
    [/(?:u8|[uUL])?"(?:[^"\\\n]|\\.)*"/y, "str"],
    [/(?:u8|[uUL])?'(?:[^'\\\n]|\\.)*'/y, "str"],
    [
      /\b(?:alignas|alignof|auto|bool|break|case|catch|char|char8_t|char16_t|char32_t|class|co_await|co_return|co_yield|concept|const|const_cast|consteval|constexpr|constinit|continue|decltype|default|delete|do|double|dynamic_cast|else|enum|explicit|export|extern|false|final|float|for|friend|goto|if|inline|int|long|mutable|namespace|new|noexcept|nullptr|operator|override|private|protected|public|reinterpret_cast|requires|return|short|signed|sizeof|static|static_assert|static_cast|struct|switch|template|this|thread_local|throw|true|try|typedef|typeid|typename|union|unsigned|using|virtual|void|volatile|wchar_t|while)\b/y,
      "kw",
    ],
    [
      /\b(?:0[xX][\da-fA-F'_]+|0[bB][01'_]+|\d[\d'_]*(?:\.[\d'_]*)?(?:[eE][+-]?\d+)?)[uUlLfF]*\b/y,
      "num",
    ],
  ],
  rust: [
    [/\/\/.*/y, "com"],
    [/\/\*[\s\S]*?\*\//y, "com"],
    [/#!?\[[^\]\n]*\]/y, "dec"],
    [/[A-Za-z_]\w*!(?=\s*[({[])/y, "dec"],
    [/b?r(#*)"[\s\S]*?"\1/y, "str"],
    [/b?"(?:[^"\\]|\\[\s\S])*"/y, "str"],
    // char literal needs its closing quote, so lifetimes ('a) stay plain.
    [/b?'(?:[^'\\\n]|\\.)'/y, "str"],
    [
      /\b(?:as|async|await|break|const|continue|crate|dyn|else|enum|extern|false|fn|for|if|impl|in|let|loop|match|mod|move|mut|pub|ref|return|self|Self|static|struct|super|trait|true|type|union|unsafe|use|where|while)\b/y,
      "kw",
    ],
    [
      /\b(?:0[xob][\da-fA-F_]+|\d[\d_]*(?:\.[\d_]+)?(?:[eE][+-]?\d+)?)(?:[iu](?:8|16|32|64|128|size)|f32|f64)?\b/y,
      "num",
    ],
  ],
  // markup, for html/svg artifact source views. Attr values match only right
  // after '=' (lookbehind), so quoted prose in body text stays plain.
  xml: [
    [/<!--[\s\S]*?-->/y, "com"],
    [/<!\[CDATA\[[\s\S]*?\]\]>/y, "str"],
    [/<!DOCTYPE[^>]*>/iy, "dec"],
    [/<\/?[A-Za-z][\w.:-]*|\/?>/y, "kw"],
    [/[A-Za-z_][\w.:-]*(?==)/y, "key"],
    [/(?<==)"[^"]*"|(?<==)'[^']*'/y, "str"],
  ],
  // just enough structure for the md artifact source view.
  markdown: [
    [/^#{1,6}[ \t].*/my, "kw"],
    [/^(?:```|~~~).*/my, "dec"],
    [/`[^`\n]+`/y, "str"],
    [/^>.*/my, "com"],
    [/\[[^\]\n]*\]\([^)\n]*\)/y, "key"],
  ],
};

const ALIASES = {
  py: "python",
  python3: "python",
  js: "javascript",
  jsx: "javascript",
  ts: "javascript",
  typescript: "javascript",
  jsonc: "json",
  json5: "json",
  cs: "csharp",
  "c#": "csharp",
  "c++": "cpp",
  c: "cpp",
  cc: "cpp",
  cxx: "cpp",
  h: "cpp",
  hpp: "cpp",
  rs: "rust",
  html: "xml",
  htm: "xml",
  svg: "xml",
  xhtml: "xml",
  md: "markdown",
};

const resolve = (lang) => {
  const l = (lang || "").toLowerCase();
  return RULES[l] ? l : ALIASES[l] || null;
};

// best-effort sniff when the fence has no info string. Cheap on purpose:
// wrong guesses only mis-tint a block, they never change its text.
export const detectLang = (code) => {
  const t = code.trim();
  if (/^[[{"]/.test(t)) {
    try {
      JSON.parse(t);
      return "json";
    } catch {
      /* not JSON */
    }
  }
  if (/^\s*(?:def |class \w+[(:]|import \w|from \w+ import|@\w)/m.test(t)) {
    return "python";
  }
  if (/\b(?:const|let|=>|function\s*\w*\s*\()/.test(t)) return "javascript";
  return null;
};

// highlight(code, langHint) -> array of DOM nodes for a <code> element.
// Unknown/undetected language degrades to a single text node.
export const highlight = (code, langHint) => {
  const lang = resolve(langHint) || detectLang(code);
  const rules = lang && RULES[lang];
  if (!rules) return [document.createTextNode(code)];

  const out = [];
  let plain = ""; // run of unhighlighted text, flushed in one node
  const flush = () => {
    if (plain) out.push(document.createTextNode(plain));
    plain = "";
  };

  let i = 0;
  while (i < code.length) {
    let matched = false;
    for (const [rx, cls] of rules) {
      rx.lastIndex = i;
      const m = rx.exec(code);
      if (m && m[0]) {
        flush();
        const s = document.createElement("span");
        s.className = "tok-" + cls;
        s.textContent = m[0];
        out.push(s);
        i += m[0].length;
        matched = true;
        break;
      }
    }
    if (!matched) {
      // consume identifiers atomically so \b rules can't fire mid-word
      const w = /[A-Za-z_$][\w$]*/y;
      w.lastIndex = i;
      const id = w.exec(code);
      plain += id ? id[0] : code[i];
      i += id ? id[0].length : 1;
    }
  }
  flush();
  return out;
};

// highlightLines(code, langHint) -> one node array per line. Tokens that span
// lines (block comments, raw/verbatim strings) are split at each newline, so
// numbered-line views (code-artifact.js) keep token tinting on every row.
export const highlightLines = (code, langHint) => {
  const lines = [[]];
  for (const node of highlight(code, langHint)) {
    const cls = node.nodeType === Node.ELEMENT_NODE ? node.className : null;
    node.textContent.split("\n").forEach((text, j) => {
      if (j) lines.push([]);
      if (!text) return;
      if (!cls) {
        lines[lines.length - 1].push(document.createTextNode(text));
        return;
      }
      const s = document.createElement("span");
      s.className = cls;
      s.textContent = text;
      lines[lines.length - 1].push(s);
    });
  }
  return lines;
};
