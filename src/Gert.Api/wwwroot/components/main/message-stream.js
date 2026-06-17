// components/main/message-stream.js - the scrolling thread.
// Binds to state/chat.messages (van-x list); follows growth only while the
// user is pinned to the bottom - scrolling up detaches, scrolling back re-pins.
// An empty thread renders a centered hero (brand mark + starter-prompt chips
// that hand their text to the composer via chat.draft) instead of bare paper.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { BrandMark, Icon } from "../../icons/icons.js";
import { Message } from "./message.js";
import * as chat from "../../state/chat.js";
import { t } from "../../lib/i18n.js";

const { div, h2, button } = van.tags;

const STARTERS = [
  "Summarize the key points of my uploaded documents",
  "Search the web for today's top stories",
  "Write a Python script that renames files in bulk",
];

// NOT a .msg - the E2E page objects count .msg rows inside .stream.
const EmptyHero = () =>
  div(
    { class: "thread-empty" },
    div({ class: "te-mark" }, BrandMark()),
    h2(t("What are we working on?")),
    div(
      { class: "te-chips" },
      ...STARTERS.map((text) =>
        button(
          { class: "te-chip", onclick: () => (chat.draft.val = text) },
          text,
        ),
      ),
    ),
  );

export const MessageStream = component({
  name: "message-stream",
  css: `
    /* bottom padding clears the composer-wrap's 48px fade strip (composer.js)
       so a bottom-pinned message sits above the gradient, never obscured by it.
       The flex column lets an empty thread stretch so its hero can center. */
    .stream{flex:1; overflow-y:auto; padding:30px 0 68px; display:flex; flex-direction:column;}
    .thread{max-width:760px; width:100%; margin:0 auto; padding:0 30px; flex:1; display:flex; flex-direction:column;}
    .thread > *{width:100%;}

    /* empty-thread hero */
    .thread-empty{flex:1; display:flex; flex-direction:column; align-items:center; justify-content:center; gap:var(--sp-4); padding:0 30px; text-align:center; animation:rise .5s var(--ease) backwards;}
    .thread-empty .te-mark{width:34px; height:34px; opacity:.9;}
    .thread-empty .te-mark svg{width:100%; height:100%;}
    .thread-empty h2{font-family:var(--display); font-size:var(--fs-xl); font-weight:600; letter-spacing:-.01em; color:var(--ink-2);}
    .te-chips{display:flex; flex-wrap:wrap; justify-content:center; gap:var(--sp-2); max-width:560px;}
    .te-chip{font-family:var(--sans); font-size:var(--fs-md); font-weight:500; color:var(--ink-2); background:var(--surface); border:1px solid var(--line); border-radius:20px; padding:var(--sp-2) var(--sp-4); cursor:pointer; transition:var(--t-fast);}
    .te-chip:hover{border-color:var(--coral); color:var(--coral-deep); background:var(--coral-soft);}

    /* "jump to present" - floats above the composer once the reader scrolls
       up; sticky keeps it in the stream's stacking context (no portal) */
    .jump-present{position:sticky; bottom:6px; align-self:center; display:flex; align-items:center; gap:6px; margin-top:-40px; flex:none; background:var(--surface); border:1px solid var(--line); border-radius:18px; padding:7px 14px; font-family:var(--sans); font-size:var(--fs-sm); font-weight:600; color:var(--ink-2); cursor:pointer; box-shadow:var(--lift); transition:var(--t-fast); z-index:2; animation:rise .25s var(--ease) backwards;}
    /* hover keeps the pill OPAQUE: --coral-soft is a low-alpha tint, so paint
       it over the surface color instead of replacing it - the pill floats over
       message text and must never go see-through. */
    .jump-present:hover{border-color:var(--coral); color:var(--coral-deep); background:linear-gradient(var(--coral-soft), var(--coral-soft)) var(--surface);}
  `,
  view: () => {
  // pinned = the user sits at the bottom (4px tolerance for fractional scroll
  // positions). Only then does the stream follow new content; a user reading
  // back is left alone. Content growth alone fires no scroll event, so pinned
  // always reflects the user's (or our) last actual scroll.
  let pinned = true;
  // away = the reader scrolled well clear of the bottom - shows the
  // "jump to present" pill (a state, not the plain flag: it drives DOM).
  const away = van.state(false);
  const stream = div({
    class: "stream",
    onscroll: (e) => {
      const el = e.target;
      const fromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
      pinned = fromBottom < 4;
      away.val = fromBottom > 300;
    },
  });
  const thread = div(
    { class: "thread" },
    () =>
      chat.messages.length
        ? div(...chat.messages.map((m) => Message(m)))
        : EmptyHero(),
  );
  van.add(stream, thread);
  van.add(stream, () =>
    away.val
      ? button(
          {
            class: "jump-present",
            onclick: () => {
              pinned = true;
              away.val = false;
              stream.scrollTo({ top: stream.scrollHeight, behavior: "smooth" });
            },
          },
          t("Jump to present"),
          Icon("chevron", { size: 13, strokeWidth: 2.4 }),
        )
      : div(),
  );

  // Both derives are scoped to `stream` (van.derive's third arg) so they're
  // pruned once this component leaves the DOM - unscoped they'd bind to the
  // always-connected sentinel and stack up per navigation (section 12).

  // switching conversations re-pins: a freshly opened thread starts at the end.
  van.derive(
    () => {
      chat.activeId.val;
      pinned = true;
      away.val = false;
    },
    undefined,
    stream,
  );

  // keep the latest content in view while streaming. Reading the last
  // message's text inside the derive subscribes to delta updates too.
  van.derive(
    () => {
      chat.messages.length;
      chat.streaming.val;
      const last = chat.messages[chat.messages.length - 1];
      if (last) last.text; // subscribe to token deltas
      queueMicrotask(() => {
        if (pinned) stream.scrollTop = stream.scrollHeight;
      });
    },
    undefined,
    stream,
  );

  return stream;
  },
});
