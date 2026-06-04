// components/canvas/knowledge-panel.js — kb-view: header + privacy line +
// use-in-chat switch + drop zone + doc list.
import van from "van";
import { Icon } from "../../icons/icons.js";
import { Switch } from "../ui/switch.js";
import { DropZone } from "./drop-zone.js";
import { DocList } from "./doc-list.js";
import * as knowledge from "../../state/knowledge.js";
import * as ui from "../../state/ui.js";

const { section, div, h2, span } = van.tags;

const sizeLabel = (bytes) =>
  bytes > 1_048_576
    ? (bytes / 1_048_576).toFixed(1) + " MB"
    : Math.round(bytes / 1024) + " KB";

export const KnowledgePanel = () =>
  section(
    { class: () => "kb-view" + (ui.showKnowledge.val ? " active" : "") },
    div(
      { class: "panel-h" },
      div(
        { class: "row1" },
        h2("Knowledge"),
        span(
          { class: "count" },
          () =>
            `${knowledge.documents.length} docs · ${sizeLabel(knowledge.totalBytes.val)}`,
        ),
      ),
      div(
        { class: "privacy" },
        Icon("lock", { size: 11, strokeWidth: 2.2 }),
        "Private to you — stored in your own file",
      ),
    ),
    div(
      { class: "usein" },
      div(
        { class: "lab" },
        "Use in this chat ",
        div({ class: "sub" }, "retrieve from your documents"),
      ),
      Switch({
        on: () => knowledge.useInChat.val,
        onToggle: knowledge.toggleUseInChat,
      }),
    ),
    DropZone(),
    DocList(),
  );
