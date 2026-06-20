// Send a message (202 + detached turn) and consume the conversation's TurnEvent
// stream, pushing each event onto state/chat.js (+ artifacts) for reactive
// re-render. Delivery rides the best transport - SSE stream endpoint (dev-proxy
// compatible), then range polling - both sharing one seq cursor, so a fallback
// resumes without gaps or duplicates.
import * as http from "./http.js";
import * as chat from "../state/chat.js";
import type { Attachment, Citation, Message, ToolCard } from "../state/chat.js";
import type { ToolKind, WireAnswerInput, WireChatEvent, WireEventPage, WireEventType, WireMessageInput, WireTurnAccepted } from "./wire.js";
import * as artifacts from "../state/artifacts.js";
import type { Artifact } from "../state/artifacts.js";
import * as conversationsSvc from "./conversations.js";
import * as ui from "../state/ui.js";
import { activeProjectId, activeId } from "../state/chat.js";
import { selectedId } from "../state/models.js";

// One question (one tab) inside the interactive ask_user payload. `value` is the
// per-tab answer collected before submit (an option or typed text).
interface ToolQuestionItem {
  text: string;
  header: string;
  options: string[];
  allowFreeText: boolean;
  value: string;
}

// The interactive ask_user payload folded onto a tool card's `question` slot:
// one to four questions rendered as tabs, answered together. The card mutates
// the per-tab `value`, plus `posting`/`answered`/`expired`, in place.
interface ToolQuestion {
  questionId: string;
  items: ToolQuestionItem[];
  answered: boolean;
  answers: string[];
  expired: boolean;
  posting: boolean;
}

// The runtime tool-card shape: ToolCard plus the `error` line and interactive
// `question` slot (carried at runtime, not in the persisted ToolCard contract).
// Fields are narrowed to required since the tool_call push initialises them and
// the stream never writes them back to undefined. Assignable into ToolCard[].
interface LiveToolCard extends ToolCard {
  query: string;
  tag: string;
  code: string;
  stdout: string;
  todos: chat.Todo[];
  error: string;
  question: ToolQuestion | null;
}

// The streamed turn event the consumer applies is WireChatEvent (services/wire.ts) - a wide bag
// keyed by `$type`, every field optional because each event populates only its own.

// The turn ended (cancelled/error) with a question still pending: there is no
// one left to deliver an answer to - retire the card's inputs.
const retireQuestions = (assistant: Message) => {
  for (const t of assistant.tools as LiveToolCard[])
    if (t.question && !t.question.answered) t.question.expired = true;
};

// Map a ChatEvent onto the reactive `assistant` message.
const apply = (assistant: Message, event: WireEventType, data: WireChatEvent) => {
  // Same array reference - mutating through the alias stays reactive.
  const tools = assistant.tools as LiveToolCard[];
  switch (event) {
    case "message_start":
      if (data?.message_id) assistant.id = data.message_id;
      assistant.working = true;
      break;

    case "tool_call": {
      // A tool call means the model is working, not answering - show the pulse.
      assistant.working = true;
      // One card per call: the live-intent announce (name only, as args stream)
      // and the end-of-round running event (now with parsed args) share an id, so
      // an existing card with this id is UPDATED in place. Successive set_todos
      // calls also fold into the one todo card. Either way the card keeps its
      // position and the user's open/collapsed choice.
      const existing =
        tools.find((t) => t.id === data.id) ||
        (data.kind === "todo" && tools.find((t) => t.kind === "todo"));
      if (existing) {
        // A tool_call always carries id/kind; `existing` was matched by this id.
        existing.id = data.id!;
        existing.status = data.status || existing.status || "running";
        // The args land with the end-of-round event; don't clobber them with the
        // announce's empty request. The guard above ensures one of query/name
        // is a non-empty string here.
        if (data.request?.query || data.request?.name)
          existing.query = (data.request.query || data.request.name)!;
        if (data.request?.code) existing.code = data.request.code;
        break;
      }
      // a tool event's kind is a ToolKind (the wide bag also carries ArtifactKind - see WireChatEvent).
      const kind = data.kind as ToolKind;
      const card: LiveToolCard = {
        id: data.id!,
        kind,
        status: data.status || "running",
        label: labelFor(kind),
        query: data.request?.query || data.request?.name || "",
        tag: kind,
        hits: [],
        code: data.request?.code || "",
        stdout: "",
        error: "",
        todos: [],
        // ask_user's interactive payload - filled by question_asked. Declared
        // up-front so the later assignment stays reactive (van-x tracks
        // existing fields).
        question: null,
        // The todo card IS the artifact - open it so the checklist shows.
        open: kind === "todo",
      };
      tools.push(card);
      break;
    }

    case "question_asked": {
      // The ask_user tool opened a question (1..4 tabs): fold the FULL payload
      // (the tool_call request caps long strings) onto the call's card and open
      // it - the card IS the input while the turn blocks on the answers.
      const card = tools.find((t) => t.id === data.id);
      if (card) {
        // A question_asked event always carries question_id + questions.
        card.question = {
          questionId: data.question_id!,
          items: (data.questions || []).map((q) => ({
            text: q.question,
            header: q.header || "",
            options: q.options || [],
            allowFreeText: !!q.allow_free_text,
            value: "",
          })),
          answered: false,
          answers: [],
          expired: false,
          posting: false,
        };
        card.open = true;
      }
      break;
    }

    case "question_answered": {
      // Resolved (also on replay: this event after question_asked means "no
      // longer pending" - rest-api.md SSE table). Idempotent sets keep the
      // watermark-deduped replay safe.
      const card = tools.find((t) => t.id === data.id);
      if (card?.question) {
        card.question.answered = true;
        card.question.answers = data.answers || [];
      }
      break;
    }

    case "tool_result": {
      // Still working after a tool returns - the next round/answer is coming.
      assistant.working = true;
      const card = tools.find((t) => t.id === data.id);
      if (card) {
        card.status = data.status || "done";
        card.hits = data.hits || card.hits;
        card.stdout = data.stdout ?? card.stdout;
        card.error = data.error ?? card.error;
        // A failed card opens itself - the error line is the information.
        if (card.status === "error" && card.error) card.open = true;
        card.todos = data.todos || card.todos;
        // A tool_result always carries `kind`.
        card.tag =
          data.latency_ms != null
            ? `${data.kind} - ${data.latency_ms}ms`
            : data.kind!;
        // An ask_user result with no question_answered before it = the wait
        // timed out (or errored): retire the inputs; the stdout line already
        // says "The user did not respond."
        if (data.kind === "ask_user" && card.question && !card.question.answered)
          card.question.expired = true;
        // The todo card auto-collapses to its summary row once every step is
        // checked off (header + progress bar stay visible) - after a short
        // beat so the last * is seen landing. Only this transition collapses;
        // the user is free to re-open/collapse at any point and isn't fought.
        if (
          card.kind === "todo" &&
          card.todos.length &&
          card.todos.every((t) => t.status === "done")
        )
          setTimeout(() => {
            // still all-done? (a newer update may have added steps meanwhile)
            if (card.todos.length && card.todos.every((t) => t.status === "done"))
              card.open = false;
          }, 1200);
      }
      break;
    }

    case "reasoning":
      // Thinking is "working" too - the answer text hasn't started.
      assistant.working = true;
      assistant.reasoning += data.text || "";
      break;

    case "delta":
      // Answer text is streaming now: the caret takes over from the pulse.
      assistant.working = false;
      assistant.text += data.text || "";
      break;

    case "citation":
      // A citation event carries the full Citation row (wire boundary).
      assistant.citations.push({
        ordinal: data.ordinal,
        label: data.label,
        doc_id: data.doc_id,
        locator: data.locator,
      } as Citation);
      break;

    case "artifact": {
      // An artifact event carries a full Artifact row, `kind` an ArtifactKind
      // (wire boundary).
      const a = artifacts.addArtifact({
        id: data.id,
        kind: data.kind,
        name: data.name,
        content: data.content,
        problems: data.problems,
      } as Artifact);
      ui.openArtifact(a.id);
      break;
    }

    case "message_end":
      assistant.streaming = false;
      if (data?.token_count != null) assistant.tokenCount = data.token_count;
      if (data?.duration_ms != null) assistant.durationMs = data.duration_ms;
      if (data?.context_tokens != null) chat.contextTokens.val = data.context_tokens;
      break;

    case "cancelled":
      // User stop, confirmed by the server: the row is finalised `cancelled`
      // with exactly the text we already rendered. Not an error.
      assistant.streaming = false;
      assistant.cancelled = true;
      retireQuestions(assistant);
      break;

    case "error":
      assistant.streaming = false;
      assistant.text +=
        (assistant.text ? "\n\n" : "") + "_Error: " + (data?.message || "stream failed") + "_";
      retireQuestions(assistant);
      break;
    default:
      // exhaustive over WireEventType: a new event is a compile error here. At
      // runtime an unknown frame is ignored (forward-compatible streaming).
      event satisfies never;
  }
};

// Card headline per tool kind - shared with the thread-reload card rebuild
// in services/conversations.js.
export const labelFor = (kind: ToolKind): string =>
  ({
    rag: "Retrieving from your documents",
    search: "Searching the web",
    sandbox: "Running code in the sandbox",
    todo: "Updating the todo list",
    clock: "Checking the date & time",
    make_artifact: "Creating a file",
    edit_artifact: "Editing a file",
    read_artifact: "Reading a file",
    ask_user: "Asking you a question",
    fetch: "Fetching a web page",
    memory: "Saving a memory",
    sub_agent: "Running a sub-agent",
  } satisfies Record<ToolKind, string>)[kind];

// answer(questionId, answers) - deliver the user's answers (one per question,
// in question order) to the in-flight turn's pending ask_user question (202;
// 404 = the question is stale).
export const answer = (questionId: string, answers: string[]) => {
  const body: WireAnswerInput = { question_id: questionId, answers };
  return http.post(
    `/projects/${activeProjectId.val}/conversations/${activeId.val}/answer`,
    body,
  );
};

// The shared seq cursor (watermark) the transports advance in lockstep.
interface Cursor {
  seq: number;
}

const TERMINAL = new Set<WireEventType>(["message_end", "cancelled", "error"]);
const POLL_MS = 1500;
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

// Apply one TurnEvent if it advances the cursor (the seq watermark drops
// replay/live and transport-fallback duplicates). Returns true on a terminal
// event - the turn is over.
const applyTurnEvent = (
  assistant: Message,
  cursor: Cursor,
  seq: number | null,
  data: unknown,
): boolean => {
  if (!data || typeof seq !== "number" || seq <= cursor.seq) return false;
  cursor.seq = seq;
  // The frame's data is the parsed WireChatEvent (the one dynamic SSE boundary); a real turn
  // event always carries its `$type` discriminator.
  const ev = data as WireChatEvent;
  const type = ev.$type as WireEventType;
  apply(assistant, type, ev);
  return TERMINAL.has(type);
};

// SSE: the GET stream endpoint with ?after= (works through the dev proxy).
const consumeSse = async (
  pid: string,
  cid: string,
  cursor: Cursor,
  assistant: Message,
  signal: AbortSignal,
): Promise<boolean> => {
  try {
    for await (const { id, data } of http.sse(
      `/projects/${pid}/conversations/${cid}/stream?after=${cursor.seq}`,
      { signal },
    )) {
      if (applyTurnEvent(assistant, cursor, id, data)) return true;
    }
  } catch {
    /* aborted or transient - fall through */
  }
  return signal?.aborted ? true : false; // aborted -> terminal, don't fall to poll
};

// Polling: the range endpoint, like documents.js polls ingest status. The last
// resort - and the bound: if no terminal event lands within the server's max
// turn duration (+ slack), the worker is gone and no error event will ever
// come (the orphan rule covers the DB); stop and mark the bubble failed.
const consumePoll = async (
  pid: string,
  cid: string,
  cursor: Cursor,
  assistant: Message,
  signal: AbortSignal,
): Promise<boolean> => {
  const deadline = Date.now() + 6 * 60_000;
  while (Date.now() < deadline) {
    if (signal?.aborted) return true; // stop button - detach
    let page: WireEventPage | null = null;
    try {
      // The range endpoint returns a buffered page of turn-event rows (WireEventPage).
      page = await http.get<WireEventPage>(
        `/projects/${pid}/conversations/${cid}/events?after=${cursor.seq}&limit=200`,
      );
    } catch {
      /* transient - retry */
    }
    for (const te of page?.events || []) {
      if (applyTurnEvent(assistant, cursor, te.seq, te.event)) return true;
    }
    if (!page?.has_more) await sleep(POLL_MS);
  }
  apply(assistant, "error", { message: "the turn did not finish" });
  return true;
};

// Drive one turn's events into `assistant` from a cursor until terminal.
const consume = async (
  pid: string,
  cid: string,
  after: number,
  assistant: Message,
  signal: AbortSignal,
) => {
  const cursor: Cursor = { seq: after };
  if (signal?.aborted) return;
  if (await consumeSse(pid, cid, cursor, assistant, signal)) return;
  if (signal?.aborted) return;
  await consumePoll(pid, cid, cursor, assistant, signal);
};

// The in-flight turn's abort handle - the safety hatch: the normal stop path is
// the server-side cancel below, with the consumer staying attached until the
// terminal `cancelled` event renders the exact final partial.
let activeController: AbortController | null = null;

// The in-flight turn's consumer promise. A new send() awaits it before POSTing:
// a just-stopped turn settles on its terminal `cancelled` event, and the server
// persists the row BEFORE publishing that event, so the next POST can never
// race the cancel finalize into a 409.
let activeTurn: Promise<void> | null = null;

// detach() - drop the client from the in-flight turn WITHOUT cancelling it
// server-side (detached generation keeps running). Used when the user switches
// conversation (or starts a new chat) mid-stream: the consumer is torn down so
// the composer unlocks for the thread now on screen, and re-opening the
// streaming thread later resumes it live from the row's seq.
export const detach = () => {
  if (!activeController) return;
  activeController.abort();
  activeController = null;
  chat.streaming.val = false;
};

export const stop = () => {
  if (!chat.streaming.val) return;
  chat.streaming.val = false; // swap Stop -> Send immediately; finally re-confirms

  // Server-side cancel: the turn's vLLM stream is torn down, the row finalises
  // as `cancelled`, and the still-attached consumer resolves on the terminal
  // event. Only if the POST itself fails do we fall back to detaching the
  // client (the orphan rule then ages the row out server-side).
  http
    .post(`/projects/${activeProjectId.val}/conversations/${activeId.val}/cancel`)
    .catch(() => activeController?.abort());
};

// send(content, attachments) - append the user message, open an assistant
// bubble, POST (202 + cursor), then consume the detached turn's events.
// `attachments` is [{ mime_type, data }] (base64 images pasted into the
// composer) - an image alone is a valid message, text optional.
export const send = async (content: string, attachments: Attachment[] = []) => {
  const text = content.trim();
  if ((!text && !attachments.length) || chat.streaming.val) return;

  chat.addUserMessage(text, attachments);
  const assistant = chat.addAssistantMessage();
  chat.streaming.val = true;

  const pid = activeProjectId.val;
  // New chat: mint a client id (the server create-if-missing materialises the row on
  // first message). Set it active so the rest of the thread reuses the same id.
  const isNew = !activeId.val;
  const cid = activeId.val || crypto.randomUUID();
  if (!activeId.val) activeId.val = cid;
  const body: WireMessageInput = {
    content: text,
    // an image alone is a valid message; omit the key when empty (JSON drops it either way -
    // and exactOptionalPropertyTypes forbids an explicit `undefined` on the optional field).
    ...(attachments.length ? { attachments } : {}),
    // model_id is the selected provider's slug; sampling + thinking ride the provider.
    model_id: selectedId.val,
    tools: { ...chat.tools },
    // the clock tool's default zone - "what time is it" answers user-local
    timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
  };

  const ac = new AbortController();
  activeController = ac;
  let turn: Promise<void> | null = null;
  try {
    // Settle the previous turn first (stop -> send): its consumer resolves on
    // the terminal event, by which point the row is already finalised. Bounded
    // so a wedged consumer (lost cancel POST, dead worker) can't block the
    // composer forever - the worst case is then an honest 409 below.
    if (activeTurn) await Promise.race([activeTurn, sleep(10_000)]);

    // The 202 accept response (WireTurnAccepted: assistant row id + start seq).
    const accepted = await http.post<WireTurnAccepted>(
      `/projects/${pid}/conversations/${cid}/messages`,
      body,
    );
    assistant.id = accepted.assistant_message_id;
    // The server materialised the conversation row on accept - refresh the
    // sidebar NOW so the new thread is reachable if the user switches away
    // mid-stream (the finally refresh below picks up its auto-title later).
    if (isNew) conversationsSvc.list().catch(() => {});
    turn = consume(pid, cid, accepted.seq, assistant, ac.signal);
    activeTurn = turn;
    await turn;
  } catch (e) {
    // A user-initiated stop is not an error - keep whatever text streamed in.
    if (!ac.signal.aborted) {
      // The rejection is an ApiError (HTTP failure) or a generic Error; both
      // expose `message`, only ApiError carries `status`.
      const err = e as { status?: number; message?: string };
      const msg =
        err.status === 409
          ? "the previous response is still finishing - try again in a moment"
          : err.message || "stream failed";
      assistant.text += (assistant.text ? "\n\n" : "") + "_Error: " + msg + "_";
    }
  } finally {
    if (activeTurn === turn) activeTurn = null;
    // Ownership check: after a stop, a newer send() may have taken over the
    // composer state already - only the current owner restores it.
    if (activeController === ac) {
      activeController = null;
      chat.streaming.val = false;
    }
    assistant.streaming = false;
    // Refresh the sidebar once more at turn end: the server auto-titles the
    // conversation after the first message, and list() syncs that title in.
    if (isNew) conversationsSvc.list().catch(() => {});
  }
};

// resume(cid, threadMessage) - re-attach to an in-flight turn after a reload:
// the thread GET returned an assistant row with status "streaming", so replay
// its events from the row's seq and tail live. The replay carries every delta
// of the turn, so the bubble's text is rebuilt from scratch (the headline of
// detached generation: a refresh mid-turn loses nothing).
export const resume = async (cid: string, threadMessage: { id?: string; seq?: number }) => {
  if (chat.streaming.val) return;
  const assistant = chat.messages.find((m) => m.id === threadMessage.id);
  if (!assistant) return;

  // The replay carries the WHOLE turn (deltas, reasoning, tool events,
  // citations), so reset everything the thread GET pre-filled - otherwise
  // replayed tool_call events would duplicate the rebuilt cards.
  assistant.text = "";
  assistant.reasoning = "";
  assistant.tools.length = 0;
  assistant.citations.length = 0;
  assistant.streaming = true;
  chat.streaming.val = true;

  const ac = new AbortController();
  activeController = ac;
  let turn: Promise<void> | null = null;
  try {
    // A resumed (status "streaming") row always carries its cursor seq.
    turn = consume(activeProjectId.val, cid, threadMessage.seq!, assistant, ac.signal);
    activeTurn = turn;
    await turn;
  } finally {
    if (activeTurn === turn) activeTurn = null;
    if (activeController === ac) {
      activeController = null;
      chat.streaming.val = false;
    }
    assistant.streaming = false;
  }
};
