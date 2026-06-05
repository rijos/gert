// components/main/tool-card.js — toolzone node card: expandable, done state,
// doc-hits, query, stdout. Binds to one reactive tool entry on a message.
import van from "van";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";

const { div, span, pre } = van.tags;

const iconFor = (kind) =>
  ({ rag: "search", search: "globe", sandbox: "file" })[kind] || "file";

const DocHit = (h) =>
  div(
    { class: "doc-hit" },
    Icon("file", { size: 13, class: "fi", strokeWidth: 2 }),
    span({ class: "dn" }, h.doc || h.title || ""),
    h.page ? span({ class: "pg" }, h.page) : null,
    h.score != null ? span({ class: "score" }, String(h.score)) : null,
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
  `,
  // `card` is a reactive tool entry: { kind, status, label, tag, query, hits, stdout, open }
  view: (card) =>
    div(
      { class: () => "tcard" + (card.status === "done" ? " done" : "") },
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
        () => (card.stdout ? pre({ class: "stdout" }, card.stdout) : div()),
      ),
    ),
});
