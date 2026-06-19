// components/canvas/doc-row.js - file icon + meta + status pill + trash.
// Binds to one reactive document; the filename node uses unicode-bidi:isolate
// (CSS) for anti-spoofing - it is a van text node, XSS-safe by construction.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { Pill } from "../ui/pill.js";
import * as svc from "../../services/documents.js";
import type { Document } from "../../state/knowledge.js";
import { attempt } from "../../lib/action.js";
import { fmtBytes } from "../../lib/format.js";

const { div, button } = van.tags;

const STATUS_KIND: Record<string, string> = { ready: "ready", processing: "proc", failed: "fail" };

const subText = (d: Document) => {
  // Document.progress is a bare chunk count per the store contract, but richer ingest
  // backends send a {done,total} object. Cast at this dynamic wire boundary so the
  // `.done ?? .total ?? ...` reads type-check (number.done is undefined -> ?? falls through).
  const p = d.progress as undefined | (number & { done?: number; total?: number });
  if (d.status === "processing")
    return p
      ? `embedding ${p.done ?? p} / ${p.total ?? d.chunk_count ?? "?"} chunks...`
      : "processing...";
  if (d.status === "failed") return d.error || "no extractable text";
  return `${fmtBytes(d.size ?? 0)} - ${d.chunk_count ?? 0} chunks`;
};

export const DocRow = component({
  name: "doc-row",
  css: `
    .doc {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 10px;
      border-radius: var(--r-sm);
      transition: var(--t-fast);
      cursor: default;
    }

    .doc:hover {
      background: var(--surface-2);
    }

    .doc .fi {
      width: 16px;
      height: 16px;
      flex: none;
      color: var(--ink-3);
    }

    .doc .meta {
      flex: 1;
      min-width: 0;
    }

    .doc .dname {
      font-size: var(--fs-sm);
      font-weight: 500;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      unicode-bidi: isolate;
    }

    .doc .dsub {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      color: var(--ink-3);
      margin-top: 1px;
    }

    .doc:hover .trash {
      opacity: 1;
    }
  `,
  view: (d: Document) =>
  div(
    { class: "doc" },
    Icon("file", { size: 16, class: "fi", strokeWidth: 1.9 }),
    div(
      { class: "meta" },
      div({ class: "dname" }, () => d.name || ""),
      div({ class: "dsub" }, () => subText(d)),
    ),
    () => Pill({ kind: (d.status ? STATUS_KIND[d.status] : undefined) || "proc" }),
    button({ class: "trash", title: "Delete", onclick: () => attempt(() => svc.remove(d.id), "Couldn't delete this document") },
      Icon("trash", { size: 14, strokeWidth: 2 }),
    ),
  ),
});
