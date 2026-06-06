// components/canvas/doc-row.js — file icon + meta + status pill + trash.
// Binds to one reactive document; the filename node uses unicode-bidi:isolate
// (CSS) for anti-spoofing — it is a van text node, XSS-safe by construction.
import van from "van";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { Pill } from "../ui/pill.js";
import * as svc from "../../services/documents.js";
import { attempt } from "../../lib/action.js";

const { div, button } = van.tags;

const STATUS_KIND = { ready: "ready", processing: "proc", failed: "fail" };

const subText = (d) => {
  if (d.status === "processing")
    return d.progress
      ? `embedding ${d.progress.done ?? d.progress} / ${d.progress.total ?? d.chunk_count ?? "?"} chunks…`
      : "processing…";
  if (d.status === "failed") return d.error || "no extractable text";
  const mb = d.size ? (d.size / 1_048_576).toFixed(d.size < 1_048_576 ? 0 : 1) : 0;
  const sizeStr = d.size && d.size < 1024 * 1024
    ? Math.round(d.size / 1024) + " KB"
    : mb + " MB";
  return `${sizeStr} · ${d.chunk_count ?? 0} chunks`;
};

export const DocRow = component({
  name: "doc-row",
  css: `
    .doc{display:flex; align-items:center; gap:10px; padding:10px; border-radius:var(--r-sm); transition:.12s; cursor:default;}
    .doc:hover{background:var(--surface-2);}
    .doc .fi{width:16px; height:16px; flex:none; color:var(--ink-3);}
    .doc .meta{flex:1; min-width:0;}
    .doc .dname{font-size:12.5px; font-weight:500; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; unicode-bidi:isolate;}
    .doc .dsub{font-family:var(--mono); font-size:10px; color:var(--ink-3); margin-top:1px;}
    .doc:hover .trash{opacity:1;}
  `,
  // `d` is one reactive document: { name, status, size, chunk_count, progress, error }
  view: (d) =>
  div(
    { class: "doc" },
    Icon("file", { size: 16, class: "fi", strokeWidth: 1.9 }),
    div(
      { class: "meta" },
      div({ class: "dname" }, () => d.name || ""),
      div({ class: "dsub" }, () => subText(d)),
    ),
    () => Pill({ kind: STATUS_KIND[d.status] || "proc" }),
    button(
      {
        class: "trash",
        title: "Delete",
        onclick: () => attempt(() => svc.remove(d.id), "Couldn't delete this document"),
      },
      Icon("trash", { size: 14, strokeWidth: 2 }),
    ),
  ),
});
