// components/main/message-actions.js - the actions row under a finished answer:
// copy / retry on the left, generation stats on the right ("312 tok - 41
// tok/s"). Retry is offered only on the thread's last message.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { copyText } from "../../lib/clipboard.js";
import * as chat from "../../state/chat.js";
import type { Message as MessageRow } from "../../state/chat.js";
import * as chatSvc from "../../services/chat.js";

const { div, button } = van.tags;

export const MessageActions = component({
  name: "message-actions",
  css: `
    .msg-actions {
      display: flex;
      align-items: center;
      gap: 2px;
      margin-top: 8px;
    }
    .ma {
      display: grid;
      place-items: center;
      width: 28px;
      height: 28px;
      border: none;
      background: none;
      border-radius: 7px;
      color: var(--ink-3);
      cursor: pointer;
      transition: var(--t-fast);
    }
    .ma:hover {
      background: var(--surface-2);
      color: var(--coral-deep);
    }
    .ma .ck {
      display: none;
    }
    .ma.copied svg {
      display: none;
    }
    .ma.copied .ck {
      display: grid;
      color: var(--coral-deep);
    }
    .msg-meta {
      margin-left: auto;
      font-family: var(--mono);
      font-size: var(--fs-xs);
      color: var(--ink-3);
    }
  `,
  // a binding so it re-renders when the message finishes streaming.
  view: (m: MessageRow) => () => {
    if (m.streaming) return div();
    const tps =
      // durationMs is number|null; `!= null` narrows it for the `> 0` compare -
      // null > 0 was already false at runtime, so the result is unchanged.
      m.tokenCount != null && m.durationMs != null && m.durationMs > 0
        ? Math.round(m.tokenCount / (m.durationMs / 1000))
        : null;
    const copyBtn = button(
      {
        class: "ma",
        title: "Copy message",
        "aria-label": "Copy message",
        onclick: () => {
          copyText(m.text || "");
          copyBtn.classList.add("copied");
          setTimeout(() => copyBtn.classList.remove("copied"), 1200);
        },
      },
      Icon("copy", { size: 14, strokeWidth: 2 }),
      Icon("check", { size: 14, strokeWidth: 2.4, class: "ck" }),
    );
    // retry only on the thread's last message - resends the user prompt that
    // produced this answer as a fresh turn (id-matched; a mid-thread answer
    // can't be retried without forking the conversation).
    const last = chat.messages[chat.messages.length - 1];
    const isLast = last && m.id != null && last.id === m.id;
    const retry = () => {
      if (chat.streaming.val) return;
      for (let i = chat.messages.length - 1; i >= 0; i--) {
        const u = chat.messages[i];
        if (u && u.role === "user") {
          chatSvc.send(
            u.text,
            (u.attachments || []).map(({ mime_type, data }) => ({ mime_type, data })),
          );
          return;
        }
      }
    };
    return div({ class: "msg-actions" },
      copyBtn,
      isLast
        ? button({ class: "ma", title: "Retry", "aria-label": "Retry", onclick: retry }, Icon("retry", { size: 14, strokeWidth: 2 }))
        : null,
      m.tokenCount != null
        ? div({ class: "msg-meta" }, `${m.tokenCount} tok` + (tps != null ? ` - ${tps} tok/s` : ""))
        : null,
    );
  },
});
