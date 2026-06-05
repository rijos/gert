// components/main/tool-card.js — toolzone node card: expandable, done state,
// doc-hits, query, stdout, todo checklist. Binds to one reactive tool entry on
// a message.
import van from "van";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";

const { div, span, pre } = van.tags;

const iconFor = (kind) =>
  ({
    rag: "search",
    search: "globe",
    sandbox: "file",
    todo: "checklist",
    clock: "clock",
  })[kind] || "file";

const DocHit = (h) =>
  div(
    { class: "doc-hit" },
    Icon("file", { size: 13, class: "fi", strokeWidth: 2 }),
    span({ class: "dn" }, h.doc || h.title || ""),
    h.page ? span({ class: "pg" }, h.page) : null,
    h.score != null ? span({ class: "score" }, String(h.score)) : null,
  );

// One checklist row: a status-shaped marker + the step text (done = struck out).
const TodoRow = (t) =>
  div(
    { class: `todo-row ${t.status}`, "data-status": t.status },
    span({ class: "tmark" }, t.status === "done" ? "✓" : t.status === "active" ? "›" : ""),
    span({ class: "ttext" }, t.text || ""),
  );

export const ToolCard = component({
  name: "tool-card",
  css: `
    .tcard{position:relative; background:var(--surface); border:1px solid var(--line-strong); border-radius:10px; margin-bottom:8px; overflow:hidden;}
    .tcard .tnode{position:absolute; left:-21px; top:14px; width:11px; height:11px; border-radius:50%; background:var(--surface); border:2px solid var(--accent);}
    .tcard.done .tnode{background:var(--accent);}
    .thead{display:flex; align-items:center; gap:9px; padding:10px 13px; cursor:pointer;}
    .thead .ic{width:15px; height:15px; color:var(--accent-deep);}
    .thead .lab{font-family:var(--mono); font-size:12px; font-weight:500; color:var(--ink);}
    .thead .tag{font-family:var(--mono); font-size:10px; color:var(--ink-faint); margin-left:auto; border:1px solid var(--line); border-radius:5px; padding:2px 6px;}
    .tbody{padding:0 13px 13px 13px; border-top:1px dashed var(--line); margin-top:0; font-size:13px; color:var(--ink-soft);}
    .tbody.hide{display:none;}
    .tbody .q{font-family:var(--mono); font-size:12px; background:var(--inset); border:1px solid var(--line); border-radius:6px; padding:7px 10px; margin:11px 0; color:var(--ink);}
    .doc-hit{display:flex; align-items:center; gap:8px; padding:6px 0; border-bottom:1px solid var(--line);}
    .doc-hit:last-child{border-bottom:none;}
    .doc-hit .fi{width:13px; height:13px; color:var(--ink-faint);}
    .doc-hit .dn{font-family:var(--mono); font-size:11.5px; color:var(--ink);}
    .doc-hit .pg{font-family:var(--mono); font-size:10.5px; color:var(--ink-faint); margin-left:auto;}
    .doc-hit .score{font-family:var(--mono); font-size:10px; color:var(--sage);}
    .stdout{font-family:var(--mono); font-size:11.5px; background:var(--inset); border:1px solid var(--line); border-left:2.5px solid var(--sage); border-radius:6px; padding:8px 11px; color:var(--ink); white-space:pre-wrap;}
    .todo-row{display:flex; align-items:baseline; gap:8px; padding:5px 0; border-bottom:1px solid var(--line);}
    .todo-row:last-child{border-bottom:none;}
    .todo-row .tmark{font-family:var(--mono); font-size:11px; width:14px; flex:none; text-align:center; color:var(--ink-faint); border:1px solid var(--line-strong); border-radius:4px; line-height:14px; height:16px;}
    .todo-row.done .tmark{color:var(--sage); border-color:var(--sage-soft); background:var(--sage-soft);}
    .todo-row.active .tmark{color:var(--accent-deep); border-color:var(--accent);}
    .todo-row .ttext{font-size:12.5px; color:var(--ink);}
    .todo-row.done .ttext{color:var(--ink-faint); text-decoration:line-through;}
    .todo-row.active .ttext{font-weight:600;}
    .tcard.err{border-color:var(--rust, #bf4727);}
  `,
  // `card` is a reactive tool entry:
  //   { kind, status, label, tag, query, hits, stdout, todos, open }
  view: (card) =>
    div(
      {
        class: () =>
          "tcard" +
          (card.status === "done" ? " done" : "") +
          (card.status === "error" ? " err" : ""),
      },
      span({ class: "tnode" }),
      div(
        { class: "thead", onclick: () => (card.open = !card.open) },
        Icon(iconFor(card.kind), { size: 15, class: "ic", strokeWidth: 2 }),
        span({ class: "lab" }, () => card.label || card.kind),
        span({ class: "tag" }, () => card.tag || card.kind),
      ),
      div(
        { class: () => "tbody" + (card.open ? "" : " hide") },
        () => (card.query ? div({ class: "q" }, card.query) : div()),
        () =>
          card.hits && card.hits.length
            ? div(...card.hits.map((h) => DocHit(h)))
            : div(),
        () =>
          card.todos && card.todos.length
            ? div({ class: "todos" }, ...card.todos.map((t) => TodoRow(t)))
            : div(),
        () => (card.stdout ? pre({ class: "stdout" }, card.stdout) : div()),
      ),
    ),
});
