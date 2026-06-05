// highlight.js — tiny regex tokenizer for fenced code blocks (no npm).
// Security F4 (same stance as markdown.js): produces real DOM nodes
// (<span class="tok-*">) from textContent, never an HTML string. Coverage is
// deliberately minimal — strings, comments, keywords, numbers — for the
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
