// components/canvas/knowledge-panel.js - kb-view: header + privacy line +
// use-in-chat switch (chat.tools.rag - chat-and-tools.md: off removes
// search_documents for the turn) + drop zone + doc list.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { Switch } from "../ui/switch.js";
import { DropZone } from "./drop-zone.js";
import { DocList } from "./doc-list.js";
import { MemoryList } from "./memory-list.js";
import { fmtBytes } from "../../lib/format.js";
import * as chat from "../../state/chat.js";
import * as knowledge from "../../state/knowledge.js";
import * as ui from "../../state/ui.js";
import { t } from "../../lib/i18n.js";

const { section, div, h2, span } = van.tags;

export const KnowledgePanel = component({
  name: "knowledge-panel",
  css: `
    .panel-h {
      padding: 18px 18px 12px;
    }

    .panel-h .row1 {
      display: flex;
      align-items: center;
      gap: 8px;
    }

    .panel-h h2 {
      font-family: var(--display);
      font-size: var(--fs-lg);
      font-weight: 600;
    }

    .panel-h .count {
      font-family: var(--mono);
      font-size: var(--fs-xs);
      color: var(--ink-3);
      margin-left: auto;
    }

    .privacy {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      color: var(--coral-deep);
      display: flex;
      align-items: center;
      gap: 5px;
      margin-top: 7px;
    }

    .privacy svg {
      width: 11px;
      height: 11px;
    }

    .usein {
      margin: 11px 18px 4px;
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 10px 12px;
      background: var(--coral-soft);
      border: 1px solid var(--coral-line);
      border-radius: var(--r-sm);
    }

    .usein .lab {
      font-size: var(--fs-sm);
      font-weight: 600;
      color: var(--coral-deep);
      flex: 1;
    }

    .usein .sub {
      font-size: var(--fs-2xs);
      color: var(--ink-2);
      font-weight: 400;
    }

    /* kb-view base display lives with the stage (canvas-panel); here just its overflow */
    .kb-view {
      overflow: hidden;
    }

    .kb-view .doclist {
      flex: 1;
    }
  `,
  view: () =>
  section(
    { class: () => "kb-view" + (ui.showKnowledge.val ? " active" : "") },
    div(
      { class: "panel-h" },
      div(
        { class: "row1" },
        h2(t("Knowledge")),
        span({ class: "count" }, () => `${knowledge.documents.length} docs - ${fmtBytes(knowledge.totalBytes.val)}`),
      ),
      div(
        { class: "privacy" },
        Icon("lock", { size: 11, strokeWidth: 2.2 }),
        t("Private to you - stored in your own file"),
      ),
    ),
    div(
      { class: "usein" },
      div(
        { class: "lab" },
        t("Use in this chat "),
        div({ class: "sub" }, t("retrieve from your documents")),
      ),
      Switch({
        on: () => !!chat.tools.rag,
        onToggle: () => chat.toggleTool("rag"),
      }),
    ),
    DropZone(),
    DocList(),
    MemoryList(),
  ),
});
