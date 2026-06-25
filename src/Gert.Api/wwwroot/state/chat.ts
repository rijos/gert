// Projects, conversations, active conversation, the message stream, streaming
// flag. Keyed collections use van-x reactive so a streamed token or single
// tool-card update re-renders just that node. No DOM, no fetch - services/* mutate this.
import van from "/lib/van.js";
import { reactive } from "/lib/van-x.js";
import * as artifacts from "./artifacts.js";
import type { MessageRole, ToolKind, WireConversation, WireMessage, WireThread } from "../services/wire.js";

export interface Project {
  id: string;
  name: string;
}

// A sidebar conversation row IS the wire conversation (the sidebar reads id/title/updated_at).
export type Conversation = WireConversation;

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

// An inline attachment (base64), user rows only: a pasted/dropped image, or a dropped
// text file (then `name` carries the filename and the mime is non-image).
export interface Attachment {
  mime_type: string;
  data: string;
  name?: string | null;
}

// van-x reactive object pushed onto `messages`; reactiveMessage below builds it from a wire row.
export interface Message {
  id: string;
  // Only user/assistant rows reach this rendered store, but the type stays
  // faithful to the contract - components branch on "user"/"assistant".
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

export const projects = reactive<Project[]>([]);
export const activeProjectId = van.state("default");

export const conversations = reactive<Conversation[]>([]);
export const activeId = van.state<string | null>(null);

export const title = van.state("New conversation");
export const messages = reactive<Message[]>([]);
export const streaming = van.state(false);

// Per-conversation tool on/off map, keyed by tool id (open Record - the server owns the id
// set, so it stays wire-compatible with ToolToggles = Record<string, boolean>). Default-enable
// is server-driven: seedToolDefaults turns every entitled tool on once the catalog
// (state/tools) loads (entitlement remains the real gate). An id absent here reads as enabled.
export const tools = reactive<Record<string, boolean>>({});

// Default-enable every entitled tool the catalog granted (called by services/tools after the
// catalog loads). Idempotent and non-destructive: a toggle the user already set is left alone.
export const seedToolDefaults = (ids: string[]) => {
  for (const id of ids) if (!(id in tools)) tools[id] = true;
};

// An unseeded id (catalog not yet loaded) reads as enabled - the default-on contract.
const enabled = (id: string) => tools[id] ?? true;

// Thinking is a property of the selected chat provider, not a per-conversation
// toggle (pick a thinking-vs-instruct provider in the picker). The model's
// reasoning streams + renders regardless (see `reasoning` on messages).

// Last completed turn's context usage (prompt+completion tokens of the final
// model round) - feeds the composer's ring.
export const contextTokens = van.state<number | null>(null);

// One-shot composer hand-off: the empty-thread starter chips (message-stream.js)
// write a prompt here; the composer consumes it and resets it to "". A signal,
// not persistent draft state.
export const draft = van.state("");

// Seed reactiveMessage builds a `Message` from: WireMessage with its one transformed field swapped -
// persisted WireTool[] calls rebuilt into ToolCard[] (services/conversations.ts toCards), the only
// wire->store transform at this seam. Plus the three runtime UI flags a synthetic seed
// (addUserMessage/addAssistantMessage) sets.
export type MessageSeed = Omit<WireMessage, "tools"> & {
  tools?: ToolCard[];
  streaming?: boolean;
  working?: boolean;
  cancelled?: boolean;
};

// Thread GET post-transform: wire thread root with messages reshaped to seeds
// (artifacts are applied separately, by services/conversations.ts).
export type ConversationSeed = Omit<WireThread, "messages" | "artifacts"> & {
  messages?: MessageSeed[];
};

// Blank slate. Artifacts belong to the active thread (like messages), so they clear
// here too - every caller that resets to "no conversation" (the new-chat button, a
// project switch, deleting/moving the open conversation) is covered in one place.
export const newConversation = () => {
  activeId.val = null;
  title.val = "New conversation";
  messages.length = 0;
  contextTokens.val = null;
  artifacts.clear();
};

export const setConversation = (conv: ConversationSeed) => {
  activeId.val = conv.id;
  title.val = conv.title || "Untitled";
  messages.length = 0;
  (conv.messages || []).forEach((m) => messages.push(reactiveMessage(m)));
  // Ring resumes from the last completed turn. findLast is ES2023; the tsconfig
  // lib is ES2022 (browsers ship it), so this boundary cast supplies its
  // signature without widening to any.
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
    // Busy phase (three-dot pulse): true while thinking / running tools / awaiting
    // the first answer token; false once answer text streams (the caret). Only
    // meaningful while `streaming`.
    working: m.working ?? false,
    // Live flag, or the persisted row status from a thread GET after reload.
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
  const m = reactiveMessage({ role: "assistant", text: "", streaming: true, working: true });
  messages.push(m);
  return m;
};

export const setTitle = (t: string) => (title.val = t);
export const toggleTool = (id: string) => (tools[id] = !enabled(id));
// Set one tool's toggle explicitly - used to flip a whole group (e.g. Canvas) to one state.
export const setTool = (id: string, on: boolean) => (tools[id] = on);

export const setProjects = (list: Project[]) => {
  projects.length = 0;
  list.forEach((p) => projects.push(p));
};

export const setConversations = (list: Conversation[]) => {
  conversations.length = 0;
  list.forEach((c) => conversations.push(c));
};
