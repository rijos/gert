// components/main/tool-card.js - one tool-call card inside a message's
// activity block: expandable, done state, doc-hits, query, stdout, todo
// checklist. Binds to one reactive tool entry on a message.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { ProgressBar } from "../ui/progress-bar.js";
import { QuestionCard } from "./question-card.js";
import type { Todo } from "../../state/chat.js";
import { hasContent, iconFor, progress, todoLabel } from "./tool-card.helpers.js";
import type { Card, Hit } from "./tool-card.helpers.js";

const { div, span, pre } = van.tags;

const DocHit = (h: Hit) =>
  div(
    { class: "doc-hit" },
    Icon("file", { size: 13, class: "fi", strokeWidth: 2 }),
    span({ class: "dn" }, h.doc || h.title || ""),
    h.page ? span({ class: "pg" }, h.page) : null,
    h.score != null ? span({ class: "score" }, String(h.score)) : null,
  );

// One checklist row: an 18px status box + the step text (done = struck out).
const TodoRow = (t: Todo) =>
  div(
    { class: `todo-row ${t.status}`, "data-status": t.status },
    span({ class: "tmark" }, t.status === "done" ? "*" : t.status === "active" ? ">" : ""),
    span({ class: "ttext" }, t.text || ""),
  );

export const ToolCard = component({
  name: "tool-card",
  css: `
    .tcard {
      position: relative;
      background: var(--card);
      border: 1px solid var(--line);
      border-radius: var(--r);
      margin-bottom: 8px;
      overflow: hidden;
      box-shadow: var(--lift);
    }
    .thead {
      display: flex;
      align-items: center;
      gap: 9px;
      padding: 10px 13px;
      cursor: pointer;
    }
    .tcard.empty .thead {
      cursor: default;
    }
    .thead .ic {
      width: 15px;
      height: 15px;
      color: var(--coral-deep);
      flex: none;
    }
    .thead .lab {
      font-family: var(--mono);
      font-size: var(--fs-sm);
      font-weight: 500;
      color: var(--ink);
      min-width: 0;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    /* n/N fraction chip */
    .thead .tcount {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      color: var(--ink-2);
      background: var(--surface-2);
      border: 1px solid var(--line);
      border-radius: 6px;
      padding: 1.5px 7px;
      flex: none;
    }
    .thead .tag {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      color: var(--ink-3);
      margin-left: auto;
      border: 1px solid var(--line);
      border-radius: 5px;
      padding: 2px 6px;
      flex: none;
    }
    /* checklist progress: coral gradient while working, solid coral once complete */
    .pbar.tprog {
      height: 3px;
      background: var(--track);
    }
    .pbar.tprog > i {
      background: linear-gradient(90deg, var(--coral), var(--coral-2));
    }
    .tcard.complete .pbar.tprog > i {
      background: var(--coral);
    }
    .tbody {
      padding: 0 13px 13px 13px;
      border-top: 1px dashed var(--line);
      margin-top: 0;
      font-size: var(--fs-md);
      color: var(--ink-2);
    }
    .tbody.hide {
      display: none;
    }
    .tbody .q {
      font-family: var(--mono);
      font-size: var(--fs-sm);
      background: var(--surface-2);
      border: 1px solid var(--line);
      border-radius: 6px;
      padding: 7px 10px;
      margin: 11px 0;
      color: var(--ink);
    }
    .doc-hit {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 6px 0;
      border-bottom: 1px solid var(--line);
    }
    .doc-hit:last-child {
      border-bottom: none;
    }
    .doc-hit .fi {
      width: 13px;
      height: 13px;
      color: var(--ink-3);
    }
    .doc-hit .dn {
      font-family: var(--mono);
      font-size: var(--fs-xs);
      color: var(--ink);
    }
    .doc-hit .pg {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      color: var(--ink-3);
      margin-left: auto;
    }
    .doc-hit .score {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      color: var(--coral-deep);
    }
    .stdout {
      font-family: var(--mono);
      font-size: var(--fs-xs);
      background: var(--surface-2);
      border: 1px solid var(--line);
      border-left: 2.5px solid var(--coral);
      border-radius: 6px;
      padding: 8px 11px;
      color: var(--ink);
      white-space: pre-wrap;
    }
    /* failure text: timeout, refused budget-exhausted call, tool defect */
    .terror {
      font-family: var(--mono);
      font-size: var(--fs-xs);
      background: var(--surface-2);
      border: 1px solid var(--line);
      border-left: 2.5px solid var(--brick);
      border-radius: 6px;
      padding: 8px 11px;
      margin-top: 11px;
      color: var(--ink);
      white-space: pre-wrap;
    }
    .todo-row {
      display: flex;
      align-items: center;
      gap: 9px;
      padding: 5px 0;
      border-bottom: 1px solid var(--line-soft);
    }
    .todo-row:last-child {
      border-bottom: none;
    }
    .todo-row .tmark {
      font-family: var(--mono);
      font-size: var(--fs-xs);
      width: 18px;
      height: 18px;
      flex: none;
      display: grid;
      place-items: center;
      color: var(--ink-3);
      border: 1px solid var(--line);
      border-radius: 5px;
    }
    .todo-row.done .tmark {
      color: var(--coral-deep);
      border-color: var(--coral-line);
      background: var(--coral-soft);
    }
    .todo-row.active .tmark {
      color: var(--coral-deep);
      border-color: var(--coral-line);
      background: var(--coral-soft);
      animation: tcard-breathe 1.6s ease-in-out infinite;
    }
    .todo-row .ttext {
      font-size: var(--fs-sm);
      color: var(--ink-2);
    }
    .todo-row.done .ttext {
      color: var(--ink-3);
      text-decoration: line-through;
    }
    .todo-row.active .ttext {
      font-weight: 600;
      color: var(--ink);
    }
    @keyframes tcard-breathe {
      50% {
        transform: scale(1.12);
        opacity: .8;
      }
    }
    /* all-done + collapsed: a single summary row that re-expands on click */
    .tsummary {
      display: flex;
      align-items: center;
      gap: 9px;
      padding: 9px 13px;
      cursor: pointer;
      font-size: var(--fs-sm);
      color: var(--ink-2);
    }
    .tsummary .tmark {
      width: 16px;
      height: 16px;
      flex: none;
      display: grid;
      place-items: center;
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      color: var(--coral-deep);
      border: 1px solid var(--coral-line);
      background: var(--coral-soft);
      border-radius: 5px;
    }
    .tsummary .tchev {
      margin-left: auto;
      color: var(--ink-3);
      flex: none;
    }
    .tcard.err {
      border-color: var(--brick);
    }
  `,
  // `card` is a reactive tool entry:
  //   { kind, status, label, tag, query, hits, stdout, todos, open }
  view: (card: Card) =>
    div(
      {
        class: () => {
          const { all } = progress(card);
          return (
            "tcard" +
            (card.status === "done" ? " done" : "") +
            (card.status === "error" ? " err" : "") +
            (all ? " complete" : "") +
            (all && !card.open ? " collapsed" : "") +
            (hasContent(card) ? "" : " empty")
          );
        },
      },
      div(
        // No body content yet (e.g. a tool still streaming its arguments) -> the
        // header is inert; expanding to an empty body invites nothing.
        { class: "thead", onclick: () => hasContent(card) && (card.open = !card.open) },
        Icon(iconFor(card.kind), { size: 15, class: "ic", strokeWidth: 2 }),
        span({ class: "lab" }, () =>
          card.kind === "todo" ? todoLabel(card) : card.label || card.kind,
        ),
        // the at-a-glance progress chip shown when collapsed.
        () => {
          const { ts, done } = progress(card);
          return ts.length ? span({ class: "tcount" }, `${done}/${ts.length}`) : "";
        },
        span({ class: "tag" }, () => card.tag || card.kind),
      ),
      // Checklist progress. Lives OUTSIDE the collapsible body so a collapsed
      // (e.g. auto-collapsed-on-done) card still shows how far the work got.
      () => {
        const { ts, done, all } = progress(card);
        if (!ts.length) return "";
        return ProgressBar({
          value: done,
          max: ts.length,
          class: "tprog",
          // solid coral fill once complete (the gradient is the working state)
          color: all ? "var(--coral)" : "",
        });
      },
      // All boxes checked + collapsed -> one summary row; click re-expands.
      () => {
        const { ts, all } = progress(card);
        if (!all || card.open) return "";
        return div(
          { class: "tsummary", onclick: () => (card.open = true) },
          span({ class: "tmark" }, "*"),
          span(`All ${ts.length} tasks complete`),
          Icon("chevron", { size: 14, class: "tchev", strokeWidth: 2.2 }),
        );
      },
      div(
        { class: () => "tbody" + (card.open ? "" : " hide") },
        // ask_user: a PENDING question (buttons / free text) is promoted out to
        // the message level so the collapsible activity can never hide it (see
        // message.js). Once answered/expired it folds back here as a quiet
        // record, matching the reloaded look.
        () =>
          card.question && (card.question.answered || card.question.expired)
            ? QuestionCard(card.question)
            : div(),
        () => (card.query && !card.question ? div({ class: "q" }, card.query) : div()),
        () => {
          // `hits` is `unknown` in the ToolCard contract (a wire boundary);
          // the rag/search tools always deliver an array of Hit rows.
          const hits = (card.hits ?? []) as Hit[];
          return hits.length ? div(...hits.map((h) => DocHit(h))) : div();
        },
        () =>
          card.todos && card.todos.length
            ? div({ class: "todos" }, ...card.todos.map((t) => TodoRow(t)))
            : div(),
        () => (card.stdout ? pre({ class: "stdout" }, card.stdout) : div()),
        () => (card.error ? pre({ class: "terror" }, card.error) : div()),
      ),
    ),
});
