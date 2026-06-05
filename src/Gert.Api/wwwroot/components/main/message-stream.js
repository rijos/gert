// components/main/message-stream.js — the scrolling thread.
// Binds to state/chat.messages (van-x list); auto-scrolls on growth.
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
  const stream = div({ class: "stream" });
  const thread = div(
    { class: "thread" },
    () => div(...chat.messages.map((m) => Message(m))),
  );
  van.add(stream, thread);

  // keep the latest content in view while streaming. Reading the last
  // message's text inside the derive subscribes to delta updates too.
  van.derive(() => {
    chat.messages.length;
    chat.streaming.val;
    const last = chat.messages[chat.messages.length - 1];
    if (last) last.text; // subscribe to token deltas
    queueMicrotask(() => {
      stream.scrollTop = stream.scrollHeight;
    });
  });

  return stream;
  },
});
