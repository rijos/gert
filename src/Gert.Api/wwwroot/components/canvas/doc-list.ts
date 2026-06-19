import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { DocRow } from "./doc-row.js";
import * as knowledge from "../../state/knowledge.js";

const { div } = van.tags;

export const DocList = component({
  name: "doc-list",
  css: `
    .doclist {
      flex: 1;
      overflow-y: auto;
      padding: 2px 12px 16px;
    }
  `,
  view: () =>
    div(
      { class: "doclist" },
      () => div(...knowledge.documents.map((d) => DocRow(d))),
    ),
});
