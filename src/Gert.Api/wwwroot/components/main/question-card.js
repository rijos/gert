// components/main/question-card.js - the interactive ask_user question inside
// a tool card (chat-and-tools.md section Ask the user): the question text, one
// button per option, and a free-text input when allowed. States: pending
// (inputs live) -> answered (chosen option highlighted / answer echoed, inputs
// retired) -> or expired (timeout / turn ended - a quiet "No response" line).
// Required prop: `q` - the reactive card.question object set by
// services/chat.js ({ questionId, text, options, allowFreeText, answered,
// answer, expired, posting }).
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import * as chatSvc from "../../services/chat.js";

const { div, button, input } = van.tags;

export const QuestionCard = component({
  name: "question-card",
  css: `
    .qcard{margin:11px 0 2px 0;}
    .qcard .qtext{font-size:13px; color:var(--ink); font-weight:600; margin-bottom:9px; white-space:pre-wrap;}
    .qcard .qopts{display:flex; flex-wrap:wrap; gap:7px; margin-bottom:9px;}
    .qcard .qopt{font-family:var(--mono); font-size:12px; color:var(--ink); background:var(--surface-2); border:1px solid var(--line); border-radius:var(--r-sm); padding:6px 12px; cursor:pointer; transition:.12s;}
    .qcard .qopt:hover:not(:disabled){border-color:var(--coral); color:var(--coral-deep);}
    .qcard .qopt:disabled{opacity:.55; cursor:not-allowed;}
    .qcard .qopt.chosen{border-color:var(--coral-line); background:var(--coral-soft); color:var(--coral-deep); opacity:1;}
    .qcard .qfree{display:flex; gap:7px;}
    .qcard .qinput{flex:1; font-size:12.5px; color:var(--ink); background:var(--surface-2); border:1px solid var(--line); border-radius:var(--r-sm); padding:6px 10px; outline:none;}
    .qcard .qinput:focus{border-color:var(--coral);}
    .qcard .qsend{font-size:12px; font-weight:600; color:var(--coral-deep); background:var(--coral-soft); border:1px solid var(--coral-line); border-radius:var(--r-sm); padding:6px 12px; cursor:pointer;}
    .qcard .qsend:disabled{opacity:.55; cursor:not-allowed;}
    .qcard .qanswer{font-family:var(--mono); font-size:12px; color:var(--coral-deep); margin-top:2px;}
    .qcard .qexpired{font-size:12px; color:var(--ink-3); font-style:italic;}
  `,
  view: (q) => {
    // -- logic -----------------------------------
    const draft = van.state("");
    const inert = () => q.answered || q.expired || q.posting;
    const submit = (value) => {
      const text = (value ?? "").trim();
      if (!text || inert()) return;
      q.posting = true;
      // The answered/disabled state comes from the question_answered event on
      // the stream, not from the 202 - one source of truth, replay included.
      chatSvc
        .answer(q.questionId, text)
        .catch((e) => {
          // 404 = stale question (it just timed out / resolved server-side).
          if (e?.status === 404) q.expired = true;
        })
        .finally(() => (q.posting = false));
    };

    // -- content ---------------------------------
    return div(
      { class: "qcard" },
      div({ class: "qtext" }, q.text),
      q.options.length
        ? div(
            { class: "qopts" },
            ...q.options.map((opt) =>
              button(
                {
                  class: () =>
                    "qopt" + (q.answered && q.answer === opt ? " chosen" : ""),
                  type: "button",
                  disabled: inert,
                  onclick: () => submit(opt),
                },
                opt,
              ),
            ),
          )
        : "",
      () =>
        q.allowFreeText && !q.answered && !q.expired
          ? div(
              { class: "qfree" },
              input({
                class: "qinput",
                type: "text",
                placeholder: "Type your answer...",
                value: draft,
                oninput: (e) => (draft.val = e.target.value),
                onkeydown: (e) => {
                  if (e.key === "Enter") submit(draft.val);
                },
              }),
              button(
                {
                  class: "qsend",
                  type: "button",
                  disabled: () => q.posting,
                  onclick: () => submit(draft.val),
                },
                "Answer",
              ),
            )
          : "",
      // A typed (non-option) answer is echoed; a chosen option highlights above.
      () =>
        q.answered && !q.options.includes(q.answer)
          ? div({ class: "qanswer" }, "* " + q.answer)
          : "",
      () =>
        q.expired && !q.answered
          ? div({ class: "qexpired" }, "No response - the assistant continued")
          : "",
    );
  },
});
