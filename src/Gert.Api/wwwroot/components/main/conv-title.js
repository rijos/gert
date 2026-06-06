// components/main/conv-title.js — editable conversation title.
import van from "van";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import * as chat from "../../state/chat.js";
import * as svc from "../../services/conversations.js";
import { attempt } from "../../lib/action.js";

const { div, input } = van.tags;

export const ConvTitle = component({
  name: "conv-title",
  css: `
    .title-wrap{flex:1; min-width:0;}
    .conv-title{font-family:var(--display); font-size:18px; font-weight:500; letter-spacing:-.01em; display:flex; align-items:center; gap:8px; cursor:text; min-width:0;}
    .conv-title .title-text{white-space:nowrap; overflow:hidden; text-overflow:ellipsis;}
    .conv-title .edit{opacity:0; width:13px; height:13px; color:var(--ink-3); transition:.14s;}
    .title-wrap:hover .edit{opacity:1;}
    .conv-title input{font-family:var(--display); font-size:18px; font-weight:500; color:var(--ink); background:var(--surface); border:1px solid var(--coral); border-radius:var(--r-sm); padding:2px 8px; outline:none;}
  `,
  view: () => {
    // ── logic ───────────────────────────────────
    const editing = van.state(false);

    const commit = (value) => {
      editing.val = false;
      const next = value.trim();
      if (next && next !== chat.title.val && chat.activeId.val) {
        attempt(() => svc.rename(chat.activeId.val, next), "Couldn't rename this chat");
      } else if (next) {
        chat.setTitle(next);
      }
    };

    // ── content ─────────────────────────────────
    return div(
      { class: "title-wrap" },
      () =>
        editing.val
          ? input({
              value: chat.title.val,
              autofocus: true,
              onblur: (e) => commit(e.target.value),
              onkeydown: (e) => {
                if (e.key === "Enter") commit(e.target.value);
                if (e.key === "Escape") editing.val = false;
              },
            })
          : div(
              { class: "conv-title", onclick: () => (editing.val = true) },
              van.tags.span({ class: "title-text" }, chat.title.val),
              Icon("edit", { size: 13, class: "edit", strokeWidth: 2 }),
            ),
    );
  },
});
