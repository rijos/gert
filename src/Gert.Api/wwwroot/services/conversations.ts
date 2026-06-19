// List / create / rename / delete conversations, project-scoped under
// /api/projects/{pid}/conversations. Updates state/chat.js.
import * as http from "./http.js";
import * as chat from "../state/chat.js";
import type { ConversationSeed, MessageSeed, ToolCard } from "../state/chat.js";
import type { WireConversation, WireConversationInput, WireMessage, WireThread, WireTool } from "./wire.js";
import * as artifacts from "../state/artifacts.js";
import * as chatSvc from "./chat.js";

const pid = () => chat.activeProjectId.val;

// A rebuilt tool card - the runtime card shape (a superset of ToolCard: it also
// carries `error` and the interactive `question` slot the live stream fills),
// so it assigns into a message's `tools: ToolCard[]`.
interface ToolCardEx extends ToolCard {
  error: string;
  question: null;
}

// Rebuild a message's tool cards from the thread GET's persisted calls (WireTool[]) - the
// reload twin of the live tool_call/tool_result mapping in services/chat.js.
// Successive set_todos calls fold into ONE card exactly like the live stream:
// the latest call replaces the first todo card in place (position kept).
const toCards = (tools: WireTool[] | undefined): ToolCardEx[] => {
  const cards: ToolCardEx[] = [];
  for (const t of tools || []) {
    const card: ToolCardEx = {
      id: t.id,
      kind: t.kind,
      status: t.status || "done",
      label: chatSvc.labelFor(t.kind),
      tag: t.latency_ms != null ? `${t.kind} - ${t.latency_ms}ms` : t.kind,
      query: t.query || "",
      hits: t.hits || [],
      code: t.code || "",
      stdout: t.stdout ?? "",
      error: t.error ?? "",
      todos: t.todos || [],
      // A reloaded ask_user card is read-only (the question resolved with the
      // turn); query/stdout carry the question + answer. Live pending state is
      // only ever rebuilt by the resume() event replay below.
      question: null,
      // A failed card arrives open - the error line is the information.
      open: t.status === "error" && !!t.error,
    };
    const fold = t.kind === "todo" ? cards.findIndex((c) => c.kind === "todo") : -1;
    if (fold >= 0) cards[fold] = card;
    else cards.push(card);
  }
  return cards;
};

export const list = async () => {
  // GET conversations returns the sidebar rows (WireConversation - a Conversation is its subset).
  const items = await http.get<WireConversation[]>(`/projects/${pid()}/conversations`);
  chat.setConversations(items);
  // Keep the header in sync: the server may have titled the active conversation
  // since we last looked (e.g. auto-title after the first message materialised it).
  const active = items.find((c) => c.id === chat.activeId.val);
  if (active?.title) chat.setTitle(active.title);
  return items;
};

// Monotonic ticket for open(): rapid switches leave several GETs in flight and
// only the NEWEST may apply - a stale one landing last would render the wrong
// thread (and its resume would fight the live one).
let openTicket = 0;

export const open = async (id: string) => {
  const ticket = ++openTicket;
  // Switching mid-stream: detach the client consumer first (the server turn
  // keeps running detached). Without this the old consumer keeps the global
  // streaming flag pinned - locking the composer, mistargeting stop(), and
  // blocking the resume below. Re-opening the streaming thread re-attaches.
  chatSvc.detach();
  // GET thread returns the flattened WireThread: the conversation root + its WireMessage rows
  // (each with persisted WireTool calls + WireCitation rows) + persisted artifacts.
  const conv = await http.get<WireThread>(`/projects/${pid()}/conversations/${id}`);
  if (ticket !== openTicket) return conv; // superseded by a newer open
  // Map the wire thread onto the store seed: rebuild each message's persisted tool calls into
  // card shape BEFORE setConversation wraps the messages reactively. Spreading keeps every wire
  // scalar (incl. the WireCitation rows, which are Citation-compatible) and only swaps `tools`.
  const seed: ConversationSeed = {
    ...conv,
    messages: (conv.messages || []).map(
      (m): MessageSeed => ({ ...m, tools: toCards(m.tools) }),
    ),
  };
  chat.setConversation(seed);
  artifacts.setArtifacts(conv.artifacts || []);
  // `tools` is the persisted ToolToggles map - one line per known id.
  const t = conv.tools;
  if (t) {
    chat.tools.rag = !!t.rag;
    chat.tools.search = !!t.search;
    chat.tools.sandbox = !!t.sandbox;
    chat.tools.todo = !!t.todo;
    chat.tools.clock = !!t.clock;
    chat.tools.make_artifact = !!t.make_artifact;
    chat.tools.edit_artifact = !!t.edit_artifact;
    chat.tools.read_artifact = !!t.read_artifact;
    chat.tools.ask_user = !!t.ask_user;
    chat.tools.fetch = !!t.fetch;
    chat.tools.memory = !!t.memory;
  }
  // Detached turns: a still-streaming assistant row means the worker is busy on
  // this conversation - re-attach and let the bubble fill in live (the server
  // applies the orphan rule, so an abandoned row reads "error", not "streaming").
  // Do NOT "optimize" this replay away: a pending ask_user question exists ONLY
  // in turn_events (its tool_calls row lands when the call returns), so the
  // resume replay is the one path that can re-render the interactive card.
  // findLast is ES2023; the tsconfig lib is ES2022 (browsers ship it), so this
  // boundary cast supplies its signature without widening to any (mirrors
  // state/chat.ts setConversation).
  const inFlight = (
    (conv.messages || []) as WireMessage[] & {
      findLast(p: (m: WireMessage) => boolean): WireMessage | undefined;
    }
  ).findLast((m) => m.status === "streaming");
  if (inFlight) chatSvc.resume(conv.id, inFlight).catch(() => {});
  return conv;
};

export const create = async (body: WireConversationInput = {}) => {
  const conv = await http.post<WireConversation>(`/projects/${pid()}/conversations`, body);
  await list();
  chat.setConversation(conv);
  return conv;
};

export const rename = async (id: string, title: string) => {
  await http.patch(`/projects/${pid()}/conversations/${id}`, { title });
  const c = chat.conversations.find((x) => x.id === id);
  if (c) c.title = title;
  if (chat.activeId.val === id) chat.setTitle(title);
};

// Relocate a chat to another of the caller's projects; the row leaves this
// project's list. 409s while a turn streams (the server refuses mid-stream
// moves), surfaced by the caller's attempt() toast.
export const move = async (id: string, targetPid: string) => {
  await http.post(`/projects/${pid()}/conversations/${id}/move`, { target_pid: targetPid });
  const i = chat.conversations.findIndex((x) => x.id === id);
  if (i >= 0) chat.conversations.splice(i, 1);
  if (chat.activeId.val === id) {
    chatSvc.detach();
    chat.newConversation();
  }
};

export const remove = async (id: string) => {
  await http.del(`/projects/${pid()}/conversations/${id}`);
  const i = chat.conversations.findIndex((x) => x.id === id);
  if (i >= 0) chat.conversations.splice(i, 1);
  if (chat.activeId.val === id) {
    chatSvc.detach(); // it may have been mid-stream - unpin the composer
    chat.newConversation();
  }
};
