// components/main/tool-card.js — toolzone node card: expandable, done state,
// doc-hits, query, stdout. Binds to one reactive tool entry on a message.
import van from "van";
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

// `card` is a reactive tool entry: { kind, status, label, tag, query, hits, stdout, open }
export const ToolCard = (card) =>
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
  );
