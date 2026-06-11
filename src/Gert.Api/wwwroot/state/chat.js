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
  // The canvas artifact suite — on by default; toggled as one unit (see below).
  make_artifact: true,
  edit_artifact: true,
  read_artifact: true,
  // ask_user / fetch / memory — on by default (low blast radius; the JWT
  // entitlement is the real gate).
  ask_user: true,
  fetch: true,
  memory: true,
});

// The make/edit/read artifact tools are one feature ("Canvas"); the menu shows a
// single switch that flips all three together.
export const CANVAS_TOOL_IDS = ["make_artifact", "edit_artifact", "read_artifact"];
export const canvasOn = () => CANVAS_TOOL_IDS.every((id) => tools[id]);
export const toggleCanvas = () => {
  const on = !canvasOn();
  CANVAS_TOOL_IDS.forEach((id) => (tools[id] = on));
};

// per-conversation reasoning toggles (persisted server-side on send)
export const thinking = van.state(true); // ON by default — the model's native behavior
export const preserveThinking = van.state(false);

// context usage of the conversation's last completed turn (prompt+completion
// tokens of the final model round) — feeds the composer's ring.
export const contextTokens = van.state(null);

// Message shape (van-x reactive object pushed onto `messages`):
//   { id, role: "user"|"assistant", text, streaming,
//     attachments: [ { mime_type, data } ],  // pasted images (base64), user rows only
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
    attachments: m.attachments ?? [],
    reasoning: m.reasoning ?? "",
    streaming: m.streaming ?? false,
    // busy phase: true while the model is thinking / running tools / waiting for
    // the first answer token (the three-dot pulse); false once answer text
    // streams (the caret). Only meaningful while `streaming`.
    working: m.working ?? false,
    // live flag, or the persisted row status from a thread GET after reload
    cancelled: m.cancelled ?? m.status === "cancelled",
    tokenCount: m.token_count ?? null,
    durationMs: m.duration_ms ?? null,
    tools: reactive(m.tools ?? []),
    citations: reactive(m.citations ?? []),
  });

export const addUserMessage = (text, attachments = []) => {
  const m = reactiveMessage({ role: "user", text, attachments });
  messages.push(m);
  return m;
};

export const addAssistantMessage = () => {
  // Starts in the working phase (pulse) until the first answer token arrives.
  const m = reactiveMessage({ role: "assistant", text: "", streaming: true, working: true });
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
