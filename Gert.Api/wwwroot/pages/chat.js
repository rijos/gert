// pages/chat.js — the default conversation screen (/, /c/:id).
// Composes the main-region chrome; the sidebar + canvas live in AppShell.
import van from "van";
import { TopBar } from "../components/main/top-bar.js";
import { MessageStream } from "../components/main/message-stream.js";
import { Composer } from "../components/main/composer.js";
import * as conversations from "../services/conversations.js";
import * as chat from "../state/chat.js";

const { div } = van.tags;

// params.id present on /c/:id — load that conversation if not already active.
export const ChatPage = (params = {}) => {
  if (params.id && chat.activeId.val !== params.id) {
    conversations.open(params.id).catch(() => {});
  } else if (!params.id && chat.activeId.val) {
    // navigated to "/" — keep current thread; new-chat clears it explicitly
  }
  return div({ style: "display:contents" }, TopBar(), MessageStream(), Composer());
};
