// services/chat.js — send a message (202 + detached turn) and consume the
// conversation's TurnEvent stream, pushing each event onto state/chat.js
// (+ artifacts). Components bind to the state, so the typewriter, tool cards,
// citations, and canvas tabs are all just reactive re-renders of incoming
// events. Delivery rides the best available transport — WS, then the SSE
// stream endpoint (the dev-proxy-compatible path), then range polling — all
// sharing one seq cursor, so a fallback resumes without gaps or duplicates.
import * as http from "./http.js";
import * as chat from "../state/chat.js";
import * as artifacts from "../state/artifacts.js";
import * as conversationsSvc from "./conversations.js";
import * as ui from "../state/ui.js";
import { activeProjectId, activeId } from "../state/chat.js";
import { selectedId } from "../state/models.js";

// The turn ended (cancelled/error) with a question still pending: there is no
// one left to deliver an answer to — retire the card's inputs.
const retireQuestions = (assistant) => {
  for (const t of assistant.tools)
    if (t.question && !t.question.answered) t.question.expired = true;
};

// Map a ChatEvent onto state. `assistant` is the reactive message object.
const apply = (assistant, event, data) => {
  switch (event) {
    case "message_start":
      if (data?.message_id) assistant.id = data.message_id;
      assistant.working = true;
      break;

    case "tool_call": {
      // A tool call means the model is working, not answering — show the pulse.
      assistant.working = true;
      // One card per call: the live-intent announce (name only, as args stream)
      // and the end-of-round running event (now with parsed args) share an id, so
      // an existing card with this id is UPDATED in place. Successive set_todos
      // calls also fold into the one todo card. Either way the card keeps its
      // position and the user's open/collapsed choice.
      const existing =
        assistant.tools.find((t) => t.id === data.id) ||
        (data.kind === "todo" && assistant.tools.find((t) => t.kind === "todo"));
      if (existing) {
        existing.id = data.id;
        existing.status = data.status || existing.status || "running";
        // The args land with the end-of-round event; don't clobber them with the
        // announce's empty request.
        if (data.request?.query || data.request?.name)
          existing.query = data.request.query || data.request.name;
        if (data.request?.code) existing.code = data.request.code;
        break;
      }
      assistant.tools.push({
        id: data.id,
        kind: data.kind,
        status: data.status || "running",
        label: labelFor(data.kind),
        query: data.request?.query || data.request?.name || "",
        tag: data.kind,
        hits: [],
        code: data.request?.code || "",
        stdout: "",
        error: "",
        todos: [],
        // ask_user's interactive payload — filled by question_asked. Declared
        // up-front so the later assignment stays reactive (van-x tracks
        // existing fields).
        question: null,
        // The todo card IS the artifact — open it so the checklist shows.
        open: data.kind === "todo",
      });
      break;
    }

    case "question_asked": {
      // The ask_user tool opened a question: fold the FULL payload (the
      // tool_call request caps long strings) onto the call's card and open it
      // — the card IS the input while the turn blocks on the answer.
      const card = assistant.tools.find((t) => t.id === data.id);
      if (card) {
        card.question = {
          questionId: data.question_id,
          text: data.question,
          options: data.options || [],
          allowFreeText: !!data.allow_free_text,
          answered: false,
          answer: "",
          expired: false,
          posting: false,
        };
        card.open = true;
      }
      break;
    }

    case "question_answered": {
      // Resolved (also on replay: this event after question_asked means "no
      // longer pending" — rest-api.md SSE table). Idempotent boolean sets keep
      // the watermark-deduped replay safe.
      const card = assistant.tools.find((t) => t.id === data.id);
      if (card?.question) {
        card.question.answered = true;
        card.question.answer = data.answer || "";
      }
      break;
    }

    case "tool_result": {
      // Still working after a tool returns — the next round/answer is coming.
      assistant.working = true;
      const card = assistant.tools.find((t) => t.id === data.id);
      if (card) {
        card.status = data.status || "done";
        card.hits = data.hits || card.hits;
        card.stdout = data.stdout ?? card.stdout;
        card.error = data.error ?? card.error;
        // A failed card opens itself — the error line is the information.
        if (card.status === "error" && card.error) card.open = true;
        card.todos = data.todos || card.todos;
        card.tag =
          data.latency_ms != null
            ? `${data.kind} · ${data.latency_ms}ms`
            : data.kind;
        // An ask_user result with no question_answered before it = the wait
        // timed out (or errored): retire the inputs; the stdout line already
        // says "The user did not respond."
        if (data.kind === "ask_user" && card.question && !card.question.answered)
          card.question.expired = true;
        // The todo card auto-collapses to its summary row once every step is
        // checked off (header + progress bar stay visible) — after a short
        // beat so the last ✓ is seen landing. Only this transition collapses;
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
      // Thinking is "working" too — the answer text hasn't started.
      assistant.working = true;
      assistant.reasoning += data.text || "";
      break;

    case "delta":
      // Answer text is streaming now: the caret takes over from the pulse.
      assistant.working = false;
      assistant.text += data.text || "";
      break;

    case "citation":
      assistant.citations.push({
        ordinal: data.ordinal,
        label: data.label,
        doc_id: data.doc_id,
        locator: data.locator,
      });
      break;

    case "artifact": {
      const a = artifacts.addArtifact({
        id: data.id,
        kind: data.kind,
        name: data.name,
        content: data.content,
        problems: data.problems,
      });
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
  }
};

// Card headline per tool kind — shared with the thread-reload card rebuild
// in services/conversations.js.
export const labelFor = (kind) =>
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
  })[kind] || kind;

// answer(questionId, text) — deliver the user's answer to the in-flight
// turn's pending ask_user question (202; 404 = the question is stale).
export const answer = (questionId, text) =>
  http.post(
    `/projects/${activeProjectId.val}/conversations/${activeId.val}/answer`,
    { question_id: questionId, answer: text },
  );

// --- the turn consumer: one cursor, three transports -------------------------

const TERMINAL = new Set(["message_end", "cancelled", "error"]);
const POLL_MS = 1500;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Apply one TurnEvent if it advances the cursor (the seq watermark drops
// replay/live and transport-fallback duplicates). Returns true on a terminal
// event — the turn is over.
const applyTurnEvent = (assistant, cursor, seq, data) => {
  if (!data || typeof seq !== "number" || seq <= cursor.seq) return false;
  cursor.seq = seq;
  apply(assistant, data.$type, data);
  return TERMINAL.has(data.$type);
};

// WS: subscribe with the cursor; server frames are {kind:"event", seq, event}.
// Resolves true when the turn finished, false to fall back (e.g. the dev proxy
// does not speak WS).
const consumeWs = (pid, cid, cursor, assistant, signal) =>
  new Promise((resolve) => {
    if (signal?.aborted) {
      resolve(true); // stopped before we opened — treat as terminal, no fallback
      return;
    }
    let socket;
    try {
      socket = http.ws(`/projects/${pid}/conversations/${cid}/ws`);
    } catch {
      resolve(false);
      return;
    }
    let settled = false;
    const settle = (ok) => {
      if (settled) return;
      settled = true;
      try {
        socket.close();
      } catch {
        /* already closed */
      }
      resolve(ok);
    };
    // Stop button: abort closes the socket and reports terminal so consume()
    // doesn't fall through to SSE/poll. (The server turn is detached and keeps
    // running; this just detaches the client — true cancel awaits the WS
    // `cancel` message the backend has yet to register.)
    signal?.addEventListener("abort", () => settle(true), { once: true });
    socket.onopen = () =>
      socket.send(JSON.stringify({ type: "subscribe", after: cursor.seq }));
    socket.onmessage = (e) => {
      let frame;
      try {
        frame = JSON.parse(e.data);
      } catch {
        return;
      }
      if (frame?.kind !== "event") return;
      if (applyTurnEvent(assistant, cursor, frame.seq, frame.event)) settle(true);
    };
    socket.onerror = () => settle(false);
    socket.onclose = () => settle(false);
  });

// SSE: the GET stream endpoint with ?after= (works through the dev proxy).
const consumeSse = async (pid, cid, cursor, assistant, signal) => {
  try {
    for await (const { id, data } of http.sse(
      `/projects/${pid}/conversations/${cid}/stream?after=${cursor.seq}`,
      { signal },
    )) {
      if (applyTurnEvent(assistant, cursor, id, data)) return true;
    }
  } catch {
    /* aborted or transient — fall through */
  }
  return signal?.aborted ? true : false; // aborted → terminal, don't fall to poll
};

// Polling: the range endpoint, like documents.js polls ingest status. The last
// resort — and the bound: if no terminal event lands within the server's max
// turn duration (+ slack), the worker is gone and no error event will ever
// come (the orphan rule covers the DB); stop and mark the bubble failed.
const consumePoll = async (pid, cid, cursor, assistant, signal) => {
  const deadline = Date.now() + 6 * 60_000;
  while (Date.now() < deadline) {
    if (signal?.aborted) return true; // stop button — detach
    let page = null;
    try {
      page = await http.get(
        `/projects/${pid}/conversations/${cid}/events?after=${cursor.seq}&limit=200`,
      );
    } catch {
      /* transient — retry */
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
const consume = async (pid, cid, after, assistant, signal) => {
  const cursor = { seq: after };
  if (signal?.aborted) return;
  if (await consumeWs(pid, cid, cursor, assistant, signal)) return;
  if (signal?.aborted) return;
  if (await consumeSse(pid, cid, cursor, assistant, signal)) return;
  if (signal?.aborted) return;
  await consumePoll(pid, cid, cursor, assistant, signal);
};

// The in-flight turn's abort handle — the safety hatch: the normal stop path is
// the server-side cancel below, with the consumer staying attached until the
// terminal `cancelled` event renders the exact final partial.
let activeController = null;

// The in-flight turn's consumer promise. A new send() awaits it before POSTing:
// a just-stopped turn settles on its terminal `cancelled` event, and the server
// persists the row BEFORE publishing that event, so the next POST can never
// race the cancel finalize into a 409.
let activeTurn = null;

// detach() — drop the client from the in-flight turn WITHOUT cancelling it
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
  chat.streaming.val = false; // swap Stop → Send immediately; finally re-confirms

  // Server-side cancel: the turn's vLLM stream is torn down, the row finalises
  // as `cancelled`, and the still-attached consumer resolves on the terminal
  // event. Only if the POST itself fails do we fall back to detaching the
  // client (the orphan rule then ages the row out server-side).
  http
    .post(`/projects/${activeProjectId.val}/conversations/${activeId.val}/cancel`)
    .catch(() => activeController?.abort());
};

// send(content, attachments) — append the user message, open an assistant
// bubble, POST (202 + cursor), then consume the detached turn's events.
// `attachments` is [{ mime_type, data }] (base64 images pasted into the
// composer) — an image alone is a valid message, text optional.
export const send = async (content, attachments = []) => {
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
  const body = {
    content: text,
    attachments: attachments.length ? attachments : undefined,
    model_id: selectedId.val,
    tools: { ...chat.tools },
    thinking: chat.thinking.val,
    preserve_thinking: chat.preserveThinking.val,
  };

  const ac = new AbortController();
  activeController = ac;
  let turn = null;
  try {
    // Settle the previous turn first (stop → send): its consumer resolves on
    // the terminal event, by which point the row is already finalised. Bounded
    // so a wedged consumer (lost cancel POST, dead worker) can't block the
    // composer forever — the worst case is then an honest 409 below.
    if (activeTurn) await Promise.race([activeTurn, sleep(10_000)]);

    const accepted = await http.post(
      `/projects/${pid}/conversations/${cid}/messages`,
      body,
    );
    assistant.id = accepted.assistant_message_id;
    // The server materialised the conversation row on accept — refresh the
    // sidebar NOW so the new thread is reachable if the user switches away
    // mid-stream (the finally refresh below picks up its auto-title later).
    if (isNew) conversationsSvc.list().catch(() => {});
    turn = consume(pid, cid, accepted.seq, assistant, ac.signal);
    activeTurn = turn;
    await turn;
  } catch (e) {
    // A user-initiated stop is not an error — keep whatever text streamed in.
    if (!ac.signal.aborted) {
      const msg =
        e.status === 409
          ? "the previous response is still finishing — try again in a moment"
          : e.message || "stream failed";
      assistant.text += (assistant.text ? "\n\n" : "") + "_Error: " + msg + "_";
    }
  } finally {
    if (activeTurn === turn) activeTurn = null;
    // Ownership check: after a stop, a newer send() may have taken over the
    // composer state already — only the current owner restores it.
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

// resume(cid, threadMessage) — re-attach to an in-flight turn after a reload:
// the thread GET returned an assistant row with status "streaming", so replay
// its events from the row's seq and tail live. The replay carries every delta
// of the turn, so the bubble's text is rebuilt from scratch (the headline of
// detached generation: a refresh mid-turn loses nothing).
export const resume = async (cid, threadMessage) => {
  if (chat.streaming.val) return;
  const assistant = chat.messages.find((m) => m.id === threadMessage.id);
  if (!assistant) return;

  // The replay carries the WHOLE turn (deltas, reasoning, tool events,
  // citations), so reset everything the thread GET pre-filled — otherwise
  // replayed tool_call events would duplicate the rebuilt cards.
  assistant.text = "";
  assistant.reasoning = "";
  assistant.tools.length = 0;
  assistant.citations.length = 0;
  assistant.streaming = true;
  chat.streaming.val = true;

  const ac = new AbortController();
  activeController = ac;
  let turn = null;
  try {
    turn = consume(activeProjectId.val, cid, threadMessage.seq, assistant, ac.signal);
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
