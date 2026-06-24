// components/main/question-card.js - the interactive ask_user question inside a
// tool card (chat-and-tools.md section Ask the user). Up to four questions are
// answered together, rendered as TABS: one tab per question (its `header`, or
// "Question N"), each tab showing option buttons and/or a free-text input. All
// questions must be answered before submit; the answers POST in question order.
// States: pending (inputs live) -> answered (chosen answers echoed, inputs
// retired) -> or expired (timeout / turn ended - a quiet "No response" line).
// Required prop: `q` - the reactive card.question object set by services/chat.js
// ({ questionId, items[], answered, answers[], expired, posting }).
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import * as chatSvc from "../../services/chat.js";
import type { Question } from "./tool-card.helpers.js";

const { div, button, input } = van.tags;

export const QuestionCard = component({
  name: "question-card",
  css: `
    .qcard {
      margin: 11px 0 2px 0;
    }
    .qcard .qtabs {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
      margin-bottom: 9px;
    }
    .qcard .qtab {
      font-family: var(--mono);
      font-size: 11.5px;
      color: var(--ink-2);
      background: none;
      border: 1px solid transparent;
      border-radius: var(--r-sm);
      padding: 4px 9px;
      cursor: pointer;
      transition: .12s;
    }
    .qcard .qtab:hover {
      color: var(--ink);
      background: var(--surface-2);
    }
    .qcard .qtab.active {
      color: var(--coral-deep);
      background: var(--coral-soft);
      border-color: var(--coral-line);
    }
    .qcard .qtab .qdot {
      color: var(--coral);
      margin-left: 4px;
    }
    .qcard .qtext {
      font-size: 13px;
      color: var(--ink);
      font-weight: 600;
      margin-bottom: 9px;
      white-space: pre-wrap;
    }
    .qcard .qopts {
      display: flex;
      flex-wrap: wrap;
      gap: 7px;
      margin-bottom: 9px;
    }
    .qcard .qopt {
      font-family: var(--mono);
      font-size: 12px;
      color: var(--ink);
      background: var(--surface-2);
      border: 1px solid var(--line);
      border-radius: var(--r-sm);
      padding: 6px 12px;
      cursor: pointer;
      transition: .12s;
    }
    .qcard .qopt:hover:not(:disabled) {
      border-color: var(--coral);
      color: var(--coral-deep);
    }
    .qcard .qopt:disabled {
      opacity: .55;
      cursor: not-allowed;
    }
    .qcard .qopt.chosen {
      border-color: var(--coral-line);
      background: var(--coral-soft);
      color: var(--coral-deep);
      opacity: 1;
    }
    .qcard .qfree {
      display: flex;
      gap: 7px;
      margin-bottom: 9px;
    }
    .qcard .qinput {
      flex: 1;
      font-size: 12.5px;
      color: var(--ink);
      background: var(--surface-2);
      border: 1px solid var(--line);
      border-radius: var(--r-sm);
      padding: 6px 10px;
      outline: none;
    }
    .qcard .qinput:focus {
      border-color: var(--coral);
    }
    .qcard .qsend {
      font-size: 12px;
      font-weight: 600;
      color: var(--coral-deep);
      background: var(--coral-soft);
      border: 1px solid var(--coral-line);
      border-radius: var(--r-sm);
      padding: 6px 14px;
      cursor: pointer;
    }
    .qcard .qsend:disabled {
      opacity: .55;
      cursor: not-allowed;
    }
    .qcard .qanswer {
      font-family: var(--mono);
      font-size: 12px;
      color: var(--coral-deep);
      margin-top: 2px;
    }
    .qcard .qexpired {
      font-size: 12px;
      color: var(--ink-3);
      font-style: italic;
    }
  `,
  setup: (q: Question) => {
    const active = van.state(0);
    const inert = () => q.answered || q.expired || q.posting;
    // Every question needs a non-empty answer before submit.
    const complete = () => q.items.every((it) => (it.value ?? "").trim().length > 0);
    const tabLabel = (i: number) => (q.items[i]?.header ?? "").trim() || `Question ${i + 1}`;

    const submit = () => {
      if (inert() || !complete()) return;
      q.posting = true;
      const answers = q.items.map((it) => it.value.trim());
      // The answered/disabled state comes from the question_answered event on
      // the stream, not from the 202 - one source of truth, replay included.
      chatSvc
        .answer(q.questionId, answers)
        .catch((e: unknown) => {
          // 404 = stale question (it just timed out / resolved server-side).
          // The rejection is an ApiError carrying a numeric `status`.
          if ((e as { status?: number } | null)?.status === 404) q.expired = true;
        })
        .finally(() => (q.posting = false));
    };

    return { active, inert, complete, tabLabel, submit };
  },
  view: ({ active, inert, complete, tabLabel, submit }, q: Question) =>
    div(
      { class: "qcard" },
      // The tab strip - only shown for more than one question (a single
      // question keeps the original single-prompt look).
      () =>
        q.items.length > 1 && !q.answered && !q.expired
          ? div(
              { class: "qtabs", role: "tablist", "aria-label": "Questions" },
              ...q.items.map((it, i) =>
                button(
                  {
                    id: "q-tab-" + i,
                    class: () => "qtab" + (active.val === i ? " active" : ""),
                    type: "button",
                    role: "tab",
                    "aria-selected": () => String(active.val === i),
                    "aria-controls": "q-panel",
                    onclick: () => (active.val = i),
                  },
                  tabLabel(i),
                  // A dot marks tabs still awaiting an answer.
                  () =>
                    (it.value ?? "").trim() ? "" : van.tags.span({ class: "qdot" }, "*"),
                ),
              ),
            )
          : "",

      // The active question (its prompt + inputs). For a single question this is
      // just the one prompt; for many, the selected tab.
      () => {
        const i = q.items.length ? active.val : 0;
        const it = q.items[i];
        if (!it) return div();
        return div(
          // Tie the panel to its tab for screen readers (only when tabs render).
          q.items.length > 1 && !q.answered && !q.expired
            ? { role: "tabpanel", id: "q-panel", "aria-labelledby": () => "q-tab-" + active.val }
            : {},
          div({ class: "qtext" }, it.text),
          it.options.length
            ? div(
                { class: "qopts" },
                ...it.options.map((opt: string) =>
                  button(
                    {
                      class: () => "qopt" + (it.value === opt ? " chosen" : ""),
                      type: "button",
                      disabled: inert,
                      onclick: () => {
                        it.value = opt;
                      },
                    },
                    opt,
                  ),
                ),
              )
            : "",
          it.allowFreeText && !q.answered && !q.expired
            ? div(
                { class: "qfree" },
                input({
                  class: "qinput",
                  type: "text",
                  placeholder: "Type your answer...",
                  "aria-label": tabLabel(i),
                  // Function-valued (reactive) binding, NOT a direct read: a
                  // direct `it.value` here would make the enclosing panel derive
                  // depend on it, so every keystroke rebuilt the <input> and
                  // stole focus. As a function it updates the live element in place.
                  value: () => it.value,
                  oninput: (e: Event) => {
                    it.value = (e.target as HTMLInputElement).value;
                  },
                  onkeydown: (e: KeyboardEvent) => {
                    if (e.key === "Enter") submit();
                  },
                }),
              )
            : "",
        );
      },

      // The action button. A single question (or the LAST tab of a multi-
      // question card) submits all answers together; an earlier tab advances to
      // the next question with "Next", live once the current tab is answered.
      () => {
        if (q.answered || q.expired) return "";
        const multi = q.items.length > 1;
        const onLast = active.val >= q.items.length - 1;
        if (multi && !onLast) {
          const cur = q.items[active.val];
          return button(
            {
              class: "qsend",
              type: "button",
              disabled: () => q.posting || (cur?.value ?? "").trim().length === 0,
              onclick: () => (active.val += 1),
            },
            "Next",
          );
        }
        return button(
          {
            class: "qsend",
            type: "button",
            disabled: () => q.posting || !complete(),
            onclick: () => submit(),
          },
          multi ? "Submit answers" : "Answer",
        );
      },

      // Resolved: echo each question's answer.
      () =>
        q.answered
          ? div(
              ...q.items.map((_it, i) =>
                div(
                  { class: "qanswer" },
                  "* " +
                    (q.items.length > 1 ? tabLabel(i) + ": " : "") +
                    (q.answers[i] ?? ""),
                ),
              ),
            )
          : "",

      () =>
        q.expired && !q.answered
          ? div({ class: "qexpired" }, "No response - the assistant continued")
          : "",
    ),
});
