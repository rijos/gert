import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import * as chat from "../../state/chat.js";
import * as svc from "../../services/conversations.js";
import { attempt } from "../../lib/action.js";

const { div, input } = van.tags;

export const ConvTitle = component({
  name: "conv-title",
  css: `
    .title-wrap {
      flex: 1;
      min-width: 0;
    }
    .conv-title {
      font-family: var(--display);
      font-size: var(--fs-lg);
      font-weight: 500;
      letter-spacing: -.01em;
      display: flex;
      align-items: center;
      gap: 8px;
      cursor: text;
      min-width: 0;
    }
    .conv-title .title-text {
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .conv-title .edit {
      opacity: 0;
      width: 13px;
      height: 13px;
      color: var(--ink-3);
      transition: var(--t-fast);
    }
    .title-wrap:hover .edit {
      opacity: 1;
    }
    /* the edit input replaces .conv-title in place - identical type metrics and
       zero box chrome (no border/padding) so the glyphs don't move; a coral
       underline (box-shadow, not border: no layout shift) marks edit mode */
    .title-input {
      width: 100%;
      font-family: var(--display);
      font-size: var(--fs-lg);
      font-weight: 500;
      letter-spacing: -.01em;
      line-height: inherit;
      color: var(--ink);
      background: none;
      border: none;
      border-radius: 0;
      padding: 0;
      margin: 0;
      outline: none;
      box-shadow: 0 1px 0 var(--coral);
    }
  `,
  setup: () => {
    const editing = van.state(false);

    const commit = (value: string) => {
      editing.val = false;
      const next = value.trim();
      const id = chat.activeId.val;
      if (next && next !== chat.title.val && id) {
        attempt(() => svc.rename(id, next), "Couldn't rename this chat");
      } else if (next) {
        chat.setTitle(next);
      }
    };

    return { editing, commit };
  },
  view: ({ editing, commit }) =>
    div(
      { class: "title-wrap" },
      () =>
        editing.val
          ? input({
              class: "title-input",
              value: chat.title.val,
              autofocus: true,
              onfocus: (e: Event) => (e.target as HTMLInputElement).select(),
              onblur: (e: Event) => commit((e.target as HTMLInputElement).value),
              onkeydown: (e: KeyboardEvent) => {
                if (e.key === "Enter") commit((e.target as HTMLInputElement).value);
                if (e.key === "Escape") editing.val = false;
              },
            })
          : div({ class: "conv-title", onclick: () => (editing.val = true) },
              van.tags.span({ class: "title-text" }, chat.title.val),
              Icon("edit", { size: 13, class: "edit", strokeWidth: 2 }),
            ),
    ),
});
