// state/chat.js — projects, conversations, active conversation, the message
// stream, and the streaming flag. Keyed collections use van-x reactive so a
// streamed token or a single tool-card update re-renders just that node.
// No DOM, no fetch — services/* mutate this.
import van from "van";
import { reactive } from "van-x";

// projects + active project
export const projects = reactive([]); // [{ id, name }]
export const activeProjectId = van.state("default");

// conversation list (sidebar)
export const conversations = reactive([]); // [{ id, title, updated_at, ... }]
export const activeId = van.state(null);

// active conversation content
export const title = van.state("New conversation");
export const messages = reactive([]); // see message shape below
export const streaming = van.state(false);

// per-conversation tool toggles (mockup chips)
export const tools = reactive({
  rag: true,
  search: true,
  sandbox: false,
  todo: true,
  clock: true,
});

// per-conversation reasoning toggles (persisted server-side on send)
export const thinking = van.state(true); // ON by default — the model's native behavior
export const preserveThinking = van.state(false);

// context usage of the conversation's last completed turn (prompt+completion
// tokens of the final model round) — feeds the composer's ring.
export const contextTokens = van.state(null);

// Message shape (van-x reactive object pushed onto `messages`):
//   { id, role: "user"|"assistant", text, streaming,
//     tools: reactive([ { id, kind, status, label, tag, query, hits, code, stdout,
//                         todos: [{ text, status }], open } ]),
//     citations: reactive([ { ordinal, label, doc_id, locator } ]) }

export const newConversation = () => {
  activeId.val = null;
  title.val = "New conversation";
  messages.length = 0;
  thinking.val = true;
  preserveThinking.val = false;
  contextTokens.val = null;
};

export const setConversation = (conv) => {
  activeId.val = conv.id;
  title.val = conv.title || "Untitled";
  messages.length = 0;
  (conv.messages || []).forEach((m) => messages.push(reactiveMessage(m)));
  // Restore the reasoning toggles (null = server/model default).
  thinking.val = conv.thinking ?? true;
  preserveThinking.val = conv.preserve_thinking ?? false;
  // The ring resumes from the last completed turn's context footprint.
  contextTokens.val =
    (conv.messages || []).findLast((m) => m.context_tokens != null)?.context_tokens ?? null;
};

export const reactiveMessage = (m) =>
  reactive({
    id: m.id ?? crypto.randomUUID(),
    role: m.role,
    text: m.text ?? "",
    reasoning: m.reasoning ?? "",
    streaming: m.streaming ?? false,
    // live flag, or the persisted row status from a thread GET after reload
    cancelled: m.cancelled ?? m.status === "cancelled",
    tokenCount: m.token_count ?? null,
    durationMs: m.duration_ms ?? null,
    tools: reactive(m.tools ?? []),
    citations: reactive(m.citations ?? []),
  });

export const addUserMessage = (text) => {
  const m = reactiveMessage({ role: "user", text });
  messages.push(m);
  return m;
};

export const addAssistantMessage = () => {
  const m = reactiveMessage({ role: "assistant", text: "", streaming: true });
  messages.push(m);
  return m;
};

export const setTitle = (t) => (title.val = t);
export const toggleTool = (id) => (tools[id] = !tools[id]);

export const setProjects = (list) => {
  projects.length = 0;
  list.forEach((p) => projects.push(p));
};

export const setConversations = (list) => {
  conversations.length = 0;
  list.forEach((c) => conversations.push(c));
};
