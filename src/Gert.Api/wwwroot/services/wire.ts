// services/wire.ts - the SPA's single API wire contract.
//
// One place defining the shape of every JSON body the API sends and accepts, mirroring the
// Gert.Model DTOs. The wire policy is fixed in Gert.Model/Json/GertJsonOptions.cs:
// snake_case property names + STRING enums (snake_case) - so these interfaces use snake_case
// fields and the enums are string-literal unions. There are no response envelopes; a list
// endpoint returns a bare array, a single resource a bare object.
//
// This module is PURE TYPES with no imports and no runtime - it erases entirely under esbuild.
// It is the contract the typed http.* helpers deserialize into and the services map onto the
// reactive store shapes (state/*). Keep it in lockstep with the C# DTOs: when a controller's
// response shape changes, change it here in the same edit (rest-api.md is the prose companion).
//
// Naming: `Wire*` = a shape that crosses the network exactly as written. `null` = the field is
// always present but may be null; `?` = the field may be absent from the JSON.

export type MessageRole = "user" | "assistant" | "system" | "tool";
export type MessageStatus = "streaming" | "complete" | "error" | "cancelled";
export type ToolCallStatus = "running" | "done" | "error";
export type TodoStatus = "pending" | "active" | "done";
export type DocumentStatus = "processing" | "ready" | "failed";
export type ArtifactKind = "md" | "html" | "svg" | "py" | "cs" | "cpp" | "js" | "rs";
// tool-call `kind`: a builtin tool's `Id` (Gert.Tools/Builtin/*Tool.cs).
export type ToolKind =
  | "rag"
  | "search"
  | "sandbox"
  | "todo"
  | "clock"
  | "make_artifact"
  | "edit_artifact"
  | "read_artifact"
  | "ask_user"
  | "fetch"
  | "memory"
  | "sub_agent";
// turn-event `$type` discriminator (Gert.Model/Events/ChatEvent.cs).
export type WireEventType =
  | "message_start"
  | "tool_call"
  | "tool_result"
  | "reasoning"
  | "delta"
  | "citation"
  | "artifact"
  | "question_asked"
  | "question_answered"
  | "message_end"
  | "cancelled"
  | "error";
export type Theme = "light" | "dark" | "auto";
export type MemoryMode = "off" | "manual" | "auto";

// The per-conversation tool on/off map: { tool_id: boolean }. The set of ids is open (server
// owns it), so this is an open record rather than a fixed key list.
export type WireToolToggles = Record<string, boolean>;

export interface WireProjectDefaults {
  model_id?: string | null;
  tools?: WireToolToggles | null;
  reply_language?: string | null;
}

export interface WireProject {
  id: string;
  name: string;
  description?: string | null;
  instructions?: string | null;
  defaults?: WireProjectDefaults | null;
  created_at?: string;
  updated_at?: string;
  // present on the list (ProjectSummary), absent on the create/patch echo (ProjectMeta)
  conversation_count?: number;
  document_count?: number;
  memory_count?: number;
}

// POST /api/projects, PATCH /api/projects/{pid}
export interface WireProjectInput {
  name?: string;
  description?: string | null;
  instructions?: string | null;
  defaults?: WireProjectDefaults | null;
}

// The sidebar row (GET .../conversations) and the root of the thread GET share these fields.
export interface WireConversation {
  id: string;
  title?: string;
  model_id?: string;
  tools?: WireToolToggles;
  created_at?: string;
  updated_at?: string;
  archived?: boolean;
}

// GET .../conversations/{id} - a conversation plus its persisted messages and artifacts.
export interface WireThread extends WireConversation {
  messages?: WireMessage[];
  artifacts?: WireArtifact[];
}

export interface WireAttachment {
  mime_type: string;
  data: string;
}

export interface WireCitation {
  ordinal: number;
  label: string;
  doc_id?: string | null;
  locator?: string | null;
}

export interface WireHit {
  doc?: string | null;
  page?: string | null;
  score?: number | null;
  title?: string | null;
  url?: string | null;
}

export interface WireTodo {
  text: string;
  status: TodoStatus;
}

// A persisted tool call on a message row (thread GET). The live SSE tool_call/tool_result
// events carry the same fields - see WireChatEvent.
export interface WireTool {
  id: string;
  kind: ToolKind;
  status?: ToolCallStatus;
  latency_ms?: number | null;
  query?: string | null;
  code?: string | null;
  stdout?: string | null;
  hits?: WireHit[];
  todos?: WireTodo[];
  error?: string | null;
}

export interface WireMessage {
  id?: string;
  role: MessageRole;
  text?: string;
  attachments?: WireAttachment[];
  model_id?: string | null;
  status?: MessageStatus;
  seq?: number;
  reasoning?: string | null;
  token_count?: number | null;
  duration_ms?: number | null;
  context_tokens?: number | null;
  citations?: WireCitation[];
  tools?: WireTool[];
}

export interface WireArtifact {
  id: string;
  conversation_id?: string;
  message_id?: string | null;
  kind: ArtifactKind;
  name: string;
  language?: string | null;
  content: string;
  version?: number;
  created_at?: string;
}

// POST .../conversations, PATCH .../conversations/{id}
export interface WireConversationInput {
  title?: string;
  model_id?: string;
  tools?: WireToolToggles;
  archived?: boolean;
}

// POST .../messages request body.
export interface WireMessageInput {
  content: string;
  attachments?: WireAttachment[];
  // the selected provider slug, or null to ride the conversation's model
  model_id?: string | null;
  tools?: WireToolToggles;
  timezone?: string;
}

// 202 response to a message POST: the row ids + the seq the consumer's cursor starts from.
export interface WireTurnAccepted {
  conversation_id: string;
  user_message_id: string;
  assistant_message_id: string;
  seq: number;
}

// GET .../events - one buffered page of the range-poll fallback.
export interface WireEventPage {
  events?: { seq: number; event: unknown }[];
  next_cursor?: number | null;
  has_more?: boolean;
}

// One streamed turn event (the SSE `data:` object / a persisted turn_events row). A single wide
// bag keyed by `$type`; every field is optional because each event populates only its own. This
// is the one genuinely dynamic boundary - values are applied after a `$type` switch, and `hits`
// arrives as raw rows. POST .../answer carries { question_id, answer }.
export interface WireChatEvent {
  $type?: WireEventType;
  message_id?: string;
  id?: string;
  // tool_call/tool_result events carry a ToolKind; the artifact event reuses this same key for
  // an ArtifactKind. The $type switch picks which, narrowing at each use (see services/chat.ts).
  kind?: ToolKind | ArtifactKind;
  status?: ToolCallStatus;
  request?: { query?: string; name?: string; code?: string };
  question_id?: string;
  question?: string;
  options?: string[];
  allow_free_text?: boolean;
  answer?: string;
  hits?: unknown;
  stdout?: string;
  error?: string;
  todos?: WireTodo[];
  latency_ms?: number;
  text?: string;
  ordinal?: number;
  label?: string;
  doc_id?: string;
  locator?: string;
  name?: string;
  content?: string;
  problems?: string;
  token_count?: number | null;
  duration_ms?: number | null;
  context_tokens?: number | null;
  message?: string;
}

export interface WireDocument {
  id: string;
  name: string;
  mime?: string;
  size?: number;
  status?: DocumentStatus;
  chunk_count?: number;
  error?: string | null;
  // server-side ingest progress (0..1), surfaced on the poll while status is "processing"
  progress?: number;
  created_at?: string;
}

export interface WireMemoryEntry {
  id: string;
  title: string;
  content?: string | null;
  pinned?: boolean;
  updated_at?: string;
}

export interface WireMemoryInput {
  title: string;
  content: string;
  pinned?: boolean;
}

export interface WireModel {
  id: string;
  name: string;
  type?: string;
  default?: boolean;
  // null / absent = capabilities undeclared (treated permissively, mirrors the server gate)
  capabilities?: string[] | null;
  context?: number | null;
  fast?: boolean;
  endpoint?: string | null;
}

export interface WireSettings {
  theme?: Theme;
  ui_language?: string | null;
  reply_language?: string | null;
  default_model_id?: string | null;
  default_tools?: WireToolToggles | null;
  memory_mode?: MemoryMode;
}

// An artifact download ticket, served outside the turn stream.
export interface WireArtifactTicket {
  url: string;
}

export interface WireUserSummary {
  key: string;
  username?: string | null;
  size?: number;
  document_count?: number;
  last_active?: string | null;
}

export interface WireToolSpec {
  id: string;
  name: string;
  description: string;
  parameters_schema: string;
}

export interface WireSystemPrompt {
  system_prompt: string;
  tools: WireToolSpec[];
}
