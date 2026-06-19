// state/chat.js - projects, conversations, active conversation, the message
// stream, and the streaming flag. Keyed collections use van-x reactive so a
// streamed token or a single tool-card update re-renders just that node.
// No DOM, no fetch - services/* mutate this.
import van from "/lib/van.js";
import { reactive } from "/lib/van-x.js";
import type { MessageRole, ToolKind, WireConversation, WireMessage, WireThread } from "../services/wire.js";

// [{ id, name }]
export interface Project {
  id: string;
  name: string;
}

// A sidebar conversation row IS the wire conversation (the sidebar reads id/title/updated_at).
export type Conversation = WireConversation;

// A tool card under a message:
//   { id, kind, status, label, tag, query, hits, code, stdout,
//     todos: [{ text, status }], open }
export interface Todo {
  text: string;
  status: string;
}
export interface ToolCard {
  id: string;
  kind: ToolKind;
  status: string;
  label?: string;
  tag?: string;
  query?: string;
  hits?: unknown;
  code?: string;
  stdout?: string;
  todos?: Todo[];
  open?: boolean;
}

// [{ ordinal, label, doc_id, locator }] - doc_id/locator are nullable on the wire
// (a document citation has no URL locator; a web citation has no doc_id), mirroring WireCitation.
export interface Citation {
  ordinal: number;
  label: string;
  doc_id?: string | null;
  locator?: string | null;
}

// A pasted image attachment (base64), user rows only.
export interface Attachment {
  mime_type: string;
  data: string;
}

// Message shape (van-x reactive object pushed onto `messages`); see the
// reactiveMessage builder below for how a wire row maps onto these fields.
export interface Message {
  id: string;
  // the wire role (MessageRole); only user/assistant rows reach this rendered store, but the
  // type stays faithful to the contract - components branch on "user"/"assistant".
  role: MessageRole;
  text: string;
  attachments: Attachment[];
  reasoning: string;
  streaming: boolean;
  working: boolean;
  cancelled: boolean;
  tokenCount: number | null;
  durationMs: number | null;
  tools: ToolCard[];
  citations: Citation[];
}

// projects + active project
export const projects = reactive<Project[]>([]);
export const activeProjectId = van.state("default");

// conversation list (sidebar)
export const conversations = reactive<Conversation[]>([]);
export const activeId = van.state<string | null>(null);

// active conversation content
export const title = van.state("New conversation");
export const messages = reactive<Message[]>([]); // see message shape below
export const streaming = van.state(false);

// per-conversation tool toggles (mockup chips). Record<ToolKind> keeps the set == the contract.
export const tools = reactive<Record<ToolKind, boolean>>({
  rag: true,
  search: true,
  // The Python sandbox (run_python) - on by default; monty needs no container
  // infra and the JWT entitlement stays the real gate.
  sandbox: true,
  todo: true,
  clock: true,
  // The canvas artifact suite - on by default; toggled as one unit (see below).
  make_artifact: true,
  edit_artifact: true,
  read_artifact: true,
  // ask_user / fetch / memory / sub_agent - on by default (low blast radius;
  // the JWT entitlement is the real gate).
  ask_user: true,
  fetch: true,
  memory: true,
  sub_agent: true,
});

// The make/edit/read artifact tools are one feature ("Canvas"); the menu shows a
// single switch that flips all three together.
// The toggle keys of the `tools` container; the one place a string indexes it.
export type ToolId = keyof typeof tools;
export const CANVAS_TOOL_IDS: ToolId[] = ["make_artifact", "edit_artifact", "read_artifact"];
export const canvasOn = () => CANVAS_TOOL_IDS.every((id) => tools[id]);
export const toggleCanvas = () => {
  const on = !canvasOn();
  CANVAS_TOOL_IDS.forEach((id) => (tools[id] = on));
};

// Thinking is a property of the selected chat provider, not a per-conversation
// toggle (pick a thinking-vs-instruct provider in the picker). The model's
// reasoning streams + renders regardless (see `reasoning` on messages).

// context usage of the conversation's last completed turn (prompt+completion
// tokens of the final model round) - feeds the composer's ring.
export const contextTokens = van.state<number | null>(null);

// one-shot composer hand-off: the empty-thread starter chips
// (message-stream.js) write a prompt here; the composer consumes it into its
// textarea and resets it to "" (it is a signal, not persistent draft state).
export const draft = van.state("");

// The seed a `Message` is built from (by reactiveMessage below). It is a WireMessage with its one
// transformed field swapped: the persisted WireTool[] calls have been rebuilt into ToolCard[] (see
// services/conversations.ts toCards) - that is the only wire->store transform at this seam, so the
// seed is exactly `WireMessage` minus `tools`, plus the card-shaped `tools` and the three runtime
// UI flags a synthetic seed (addUserMessage/addAssistantMessage) sets.
export type MessageSeed = Omit<WireMessage, "tools"> & {
  tools?: ToolCard[];
  streaming?: boolean;
  working?: boolean;
  cancelled?: boolean;
};

// A conversation seed (thread GET, post-transform): the wire thread root with its messages
// reshaped to seeds (artifacts are applied separately, by services/conversations.ts).
export type ConversationSeed = Omit<WireThread, "messages" | "artifacts"> & {
  messages?: MessageSeed[];
};

export const newConversation = () => {
  activeId.val = null;
  title.val = "New conversation";
  messages.length = 0;
  contextTokens.val = null;
};

export const setConversation = (conv: ConversationSeed) => {
  activeId.val = conv.id;
  title.val = conv.title || "Untitled";
  messages.length = 0;
  (conv.messages || []).forEach((m) => messages.push(reactiveMessage(m)));
  // The ring resumes from the last completed turn's context footprint.
  // findLast is ES2023; the tsconfig lib is ES2022 (browsers ship it), so this
  // boundary cast supplies its signature without widening to any.
  contextTokens.val =
    ((conv.messages || []) as MessageSeed[] & {
      findLast(p: (m: MessageSeed) => boolean): MessageSeed | undefined;
    }).findLast((m) => m.context_tokens != null)?.context_tokens ?? null;
};

export const reactiveMessage = (m: MessageSeed): Message =>
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

export const addUserMessage = (text: string, attachments: Attachment[] = []): Message => {
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

export const setTitle = (t: string) => (title.val = t);
export const toggleTool = (id: ToolId) => (tools[id] = !tools[id]);

export const setProjects = (list: Project[]) => {
  projects.length = 0;
  list.forEach((p) => projects.push(p));
};

export const setConversations = (list: Conversation[]) => {
  conversations.length = 0;
  list.forEach((c) => conversations.push(c));
};
