// pages/chat.js - the default conversation screen (/, /c/:id).
// Composes the main-region chrome; the sidebar + canvas live in AppShell.
import van from "van";
import { TopBar } from "../components/main/top-bar.js";
import { MessageStream } from "../components/main/message-stream.js";
import { Composer } from "../components/main/composer.js";
import * as conversations from "../services/conversations.js";
import * as chat from "../state/chat.js";
import { attempt } from "../lib/action.js";

const { div } = van.tags;

// params.id present on /c/:id - load that conversation if not already active.
// This is the ONE open() call site for navigation (sidebar rows just navigate
// here), so switching threads can't race two opens against each other.
export const ChatPage = (params = {}) => {
  if (params.id && chat.activeId.val !== params.id) {
    attempt(() => conversations.open(params.id), "Couldn't open this chat");
  } else if (!params.id && chat.activeId.val) {
    // navigated to "/" - keep current thread; new-chat clears it explicitly
  }
  return div({ style: "display:contents" }, TopBar(), MessageStream(), Composer());
};
