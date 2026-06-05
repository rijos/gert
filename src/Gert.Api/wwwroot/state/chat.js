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

// Message shape (van-x reactive object pushed onto `messages`):
//   { id, role: "user"|"assistant", text, streaming,
//     tools: reactive([ { id, kind, status, label, tag, query, hits, code, stdout,
//                         todos: [{ text, status }], open } ]),
//     citations: reactive([ { ordinal, label, doc_id, locator } ]) }

export const newConversation = () => {
  activeId.val = null;
  title.val = "New conversation";
  messages.length = 0;
};

export const setConversation = (conv) => {
  activeId.val = conv.id;
  title.val = conv.title || "Untitled";
  messages.length = 0;
  (conv.messages || []).forEach((m) => messages.push(reactiveMessage(m)));
};

export const reactiveMessage = (m) =>
  reactive({
    id: m.id ?? crypto.randomUUID(),
    role: m.role,
    text: m.text ?? "",
    streaming: m.streaming ?? false,
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
