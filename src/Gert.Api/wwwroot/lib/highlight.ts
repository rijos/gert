// highlight.js - tiny regex tokenizer for fenced code blocks (no npm).
// Security F4 (same stance as markdown.js): produces real DOM nodes
// (<span class="tok-*">) from textContent, never an HTML string. Coverage is
// deliberately minimal - strings, comments, keywords, numbers - for the
// languages the assistant actually emits; anything else renders as plain text.

// Each rule: [sticky regex, token class]. Order matters (strings/comments
// before keywords so their contents aren't re-tokenized).
//
// BOUNDED SCANS (ReDoS defense): every multi-line lazy span is written
// [\s\S]{0,4096}? - NEVER an unbounded [\s\S]*?. Without the cap a never-closing
// opener (a wall of `/*a`, raw/triple strings) makes the outer position loop
// O(n^2): each of O(n) failed openers lazily scans to EOF. The cap makes each
// failed scan O(4096) -> the whole pass is linear. A real comment/string longer
// than the cap simply stops tinting at the cap (text is never altered); 4096
// mirrors inline.js's MATH_INLINE_MAX/MAX_DEST discipline.
// Each rule is a [sticky regex, token-class] pair; a language maps to an ordered list.
type Rule = [RegExp, string];
type RuleSet = Rule[];
const RULES: Record<string, RuleSet> = {
  json: [
    [/"(?:[^"\\]|\\.)*"(?=\s*:)/y, "key"],
    [/"(?:[^"\\]|\\.)*"/y, "str"],
    [/-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/y, "num"],
    [/\b(?:true|false|null)\b/y, "kw"],
  ],
  python: [
    [/#.*/y, "com"],
    [/[rbfu]{0,2}(?:"""[\s\S]{0,4096}?"""|'''[\s\S]{0,4096}?''')/iy, "str"],
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
    [/\/\*[\s\S]{0,4096}?\*\//y, "com"],
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
    [/\/\*[\s\S]{0,4096}?\*\//y, "com"],
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
    [/\/\*[\s\S]{0,4096}?\*\//y, "com"],
    // sticky+m: ^ only matches when the cursor sits at a line start, so this
    // fires for real directives and never on a stray mid-line '#'.
    [/^[ \t]*#[ \t]*\w+/my, "dec"],
    [/R"([^(\s]*)\(([\s\S]{0,4096}?)\)\1"/y, "str"],
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
    [/\/\*[\s\S]{0,4096}?\*\//y, "com"],
    [/#!?\[[^\]\n]*\]/y, "dec"],
    [/[A-Za-z_]\w*!(?=\s*[({[])/y, "dec"],
    [/b?r(#*)"[\s\S]{0,4096}?"\1/y, "str"],
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
  go: [
    [/\/\/.*/y, "com"],
    [/\/\*[\s\S]{0,4096}?\*\//y, "com"],
    [/`[^`]*`/y, "str"], // raw string literal (backticks, may span lines)
    [/"(?:[^"\\\n]|\\.)*"/y, "str"], // interpreted string
    [/'(?:[^'\\\n]|\\.)'/y, "str"], // rune
    [
      /\b(?:break|case|chan|const|continue|default|defer|else|fallthrough|for|func|go|goto|if|import|interface|map|package|range|return|select|struct|switch|type|var|bool|byte|complex64|complex128|error|float32|float64|int|int8|int16|int32|int64|rune|string|uint|uint8|uint16|uint32|uint64|uintptr|any|comparable|true|false|iota|nil|append|cap|close|complex|copy|delete|imag|len|make|new|panic|print|println|real|recover)\b/y,
      "kw",
    ],
    [
      /\b(?:0[xX][\da-fA-F_]+|0[bB][01_]+|0[oO][0-7_]+|\d[\d_]*(?:\.[\d_]*)?(?:[eE][+-]?\d+)?i?)\b/y,
      "num",
    ],
  ],
  // POSIX/bash shell. No heredoc rule on purpose: a backreferenced, unbounded
  // `<<EOF ... EOF` scan is exactly the kind of regex this file avoids - a body
  // line is left plain instead. Comments only when '#' starts a word (so `$#`
  // and `${x#y}` don't turn into comments mid-line).
  bash: [
    [/(?:^|(?<=\s))#.*/my, "com"],
    [/"(?:[^"\\]|\\.)*"/y, "str"],
    [/'[^']*'/y, "str"],
    [/\$\{[^}]*\}|\$[A-Za-z_]\w*|\$[#@*?!$0-9-]/y, "dec"], // variable expansions
    [
      /\b(?:if|then|elif|else|fi|for|while|until|do|done|case|esac|in|function|select|time|coproc|return|break|continue|local|export|readonly|declare|typeset|unset|shift|eval|exec|trap|set|source|alias|cd|echo|printf|read|test)\b/y,
      "kw",
    ],
    [/\b\d+\b/y, "num"],
  ],
  // markup, for html/svg artifact source views. Attr values match only right
  // after '=' (lookbehind), so quoted prose in body text stays plain.
  xml: [
    [/<!--[\s\S]{0,4096}?-->/y, "com"],
    [/<!\[CDATA\[[\s\S]{0,4096}?\]\]>/y, "str"],
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

const ALIASES: Record<string, string> = {
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
  golang: "go",
  sh: "bash",
  shell: "bash",
  zsh: "bash",
  console: "bash",
  shellsession: "bash",
  html: "xml",
  htm: "xml",
  svg: "xml",
  xhtml: "xml",
  md: "markdown",
};

const resolve = (lang: string | null | undefined) => {
  const l = (lang || "").toLowerCase();
  return RULES[l] ? l : ALIASES[l] || null;
};

// best-effort sniff when the fence has no info string. Cheap on purpose:
// wrong guesses only mis-tint a block, they never change its text.
export const detectLang = (code: string) => {
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
export const highlight = (code: string, langHint?: string | null) => {
  const lang = resolve(langHint) || detectLang(code);
  const rules = lang && RULES[lang];
  if (!rules) return [document.createTextNode(code)];

  const out: Node[] = [];
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
      // i < code.length holds (loop condition), so code[i] is a defined char.
      plain += id ? id[0] : code[i]!;
      i += id ? id[0].length : 1;
    }
  }
  flush();
  return out;
};

// highlightLines(code, langHint) -> one node array per line. Tokens that span
// lines (block comments, raw/verbatim strings) are split at each newline, so
// numbered-line views (code-artifact.js) keep token tinting on every row.
export const highlightLines = (code: string, langHint?: string | null) => {
  const lines: Node[][] = [[]];
  for (const node of highlight(code, langHint)) {
    // nodeType === ELEMENT_NODE means this node is the <span> built above, so its
    // className is the token class (cast: TS does not narrow Node by nodeType).
    const cls = node.nodeType === Node.ELEMENT_NODE ? (node as HTMLElement).className : null;
    // highlight() only emits Text and <span> nodes, whose textContent is always a string.
    node.textContent!.split("\n").forEach((text, j) => {
      if (j) lines.push([]);
      if (!text) return;
      // `lines` is seeded with one row and only ever grows, so the last row exists.
      const row = lines[lines.length - 1]!;
      if (!cls) {
        row.push(document.createTextNode(text));
        return;
      }
      const s = document.createElement("span");
      s.className = cls;
      s.textContent = text;
      row.push(s);
    });
  }
  return lines;
};
