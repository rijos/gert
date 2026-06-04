// components/canvas/doc-list.js — the scrolling document list.
// Binds to state/knowledge.documents (van-x list).
import van from "van";
import { DocRow } from "./doc-row.js";
import * as knowledge from "../../state/knowledge.js";

const { div } = van.tags;

export const DocList = () =>
  div(
    { class: "doclist" },
    () => div(...knowledge.documents.map((d) => DocRow(d))),
  );
