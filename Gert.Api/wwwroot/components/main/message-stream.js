// components/main/message-stream.js — the scrolling thread.
// Binds to state/chat.messages (van-x list); auto-scrolls on growth.
import van from "van";
import { Message } from "./message.js";
import * as chat from "../../state/chat.js";

const { div } = van.tags;

export const MessageStream = () => {
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
};
