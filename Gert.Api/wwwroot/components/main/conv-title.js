// components/main/conv-title.js — editable conversation title.
import van from "van";
import { Icon } from "../../icons/icons.js";
import * as chat from "../../state/chat.js";
import * as svc from "../../services/conversations.js";
import { attempt } from "../../lib/action.js";

const { div, input } = van.tags;

export const ConvTitle = () => {
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
};
