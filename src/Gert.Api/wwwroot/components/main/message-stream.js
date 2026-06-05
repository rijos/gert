// components/main/message-stream.js — the scrolling thread.
// Binds to state/chat.messages (van-x list); follows growth only while the
// user is pinned to the bottom — scrolling up detaches, scrolling back re-pins.
import van from "van";
import { component } from "../../lib/component.js";
import { Message } from "./message.js";
import * as chat from "../../state/chat.js";

const { div } = van.tags;

export const MessageStream = component({
  name: "message-stream",
  css: `
    .stream{flex:1; overflow-y:auto; padding:30px 0 24px;}
    .thread{max-width:760px; margin:0 auto; padding:0 30px;}
  `,
  view: () => {
  // pinned = the user sits at the bottom (4px tolerance for fractional scroll
  // positions). Only then does the stream follow new content; a user reading
  // back is left alone. Content growth alone fires no scroll event, so pinned
  // always reflects the user's (or our) last actual scroll.
  let pinned = true;
  const stream = div({
    class: "stream",
    onscroll: (e) => {
      const el = e.target;
      pinned = el.scrollHeight - el.scrollTop - el.clientHeight < 4;
    },
  });
  const thread = div(
    { class: "thread" },
    () => div(...chat.messages.map((m) => Message(m))),
  );
  van.add(stream, thread);

  // switching conversations re-pins: a freshly opened thread starts at the end.
  van.derive(() => {
    chat.activeId.val;
    pinned = true;
  });

  // keep the latest content in view while streaming. Reading the last
  // message's text inside the derive subscribes to delta updates too.
  van.derive(() => {
    chat.messages.length;
    chat.streaming.val;
    const last = chat.messages[chat.messages.length - 1];
    if (last) last.text; // subscribe to token deltas
    queueMicrotask(() => {
      if (pinned) stream.scrollTop = stream.scrollHeight;
    });
  });

  return stream;
  },
});
