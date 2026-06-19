// pages/chat.js - the default conversation screen (/, /c/:id).
// Composes the main-region chrome; the sidebar + canvas live in AppShell.
import van from "/lib/van.js";
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
// Route params from the router (lib/router.ts RouteParams = Record<string,string>);
// `/` passes {} and `/c/:id` passes the matched params.
export const ChatPage = (params: Record<string, string> = {}) => {
  // capture into a const so the truthiness narrowing to `string` survives into the
  // deferred attempt() closure (control-flow narrowing on params.id is lost there).
  const id = params.id;
  if (id && chat.activeId.val !== id) {
    attempt(() => conversations.open(id), "Couldn't open this chat");
  } else if (!id && chat.activeId.val) {
    // navigated to "/" - keep current thread; new-chat clears it explicitly
  }
  return div({ style: "display:contents" }, TopBar(), MessageStream(), Composer());
};
