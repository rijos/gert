// services/chat.js — send a message and consume the SSE ChatEvent stream,
// pushing each event onto state/chat.js (+ artifacts). Components bind to the
// state, so the typewriter, tool cards, citations, and canvas tabs are all just
// reactive re-renders of incoming events. The ONLY caller of /api here.
import { sse } from "./http.js";
import * as chat from "../state/chat.js";
import * as artifacts from "../state/artifacts.js";
import * as conversationsSvc from "./conversations.js";
import * as ui from "../state/ui.js";
import { activeProjectId, activeId } from "../state/chat.js";
import { selectedId } from "../state/models.js";

// Map an SSE event onto state. `assistant` is the reactive message object.
const apply = (assistant, event, data) => {
  switch (event) {
    case "message_start":
      if (data?.message_id) assistant.id = data.message_id;
      break;

    case "tool_call":
      assistant.tools.push({
        id: data.id,
        kind: data.kind,
        status: data.status || "running",
        label: labelFor(data.kind),
        query: data.request?.query || "",
        tag: data.kind,
        hits: [],
        code: data.request?.code || "",
        stdout: "",
        open: false,
      });
      break;

    case "tool_result": {
      const card = assistant.tools.find((t) => t.id === data.id);
      if (card) {
        card.status = "done";
        card.hits = data.hits || card.hits;
        card.stdout = data.stdout ?? card.stdout;
        card.tag =
          data.latency_ms != null
            ? `${data.kind} · ${data.latency_ms}ms`
            : data.kind;
      }
      break;
    }

    case "delta":
      assistant.text += data.text || "";
      break;

    case "citation":
      assistant.citations.push({
        ordinal: data.ordinal,
        label: data.label,
        doc_id: data.doc_id,
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
      break;

    case "error":
      assistant.streaming = false;
      assistant.text +=
        (assistant.text ? "\n\n" : "") + "_Error: " + (data?.message || "stream failed") + "_";
      break;
  }
};

const labelFor = (kind) =>
  ({
    rag: "Retrieving from your documents",
    search: "Searching the web",
    sandbox: "Running code in the sandbox",
  })[kind] || kind;

// send(content) — append the user message, open an assistant bubble, stream.
export const send = async (content) => {
  const text = content.trim();
  if (!text || chat.streaming.val) return;

  chat.addUserMessage(text);
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
    model_id: selectedId.val,
    tools: { ...chat.tools },
  };

  try {
    for await (const { event, data } of sse(
      `/projects/${pid}/conversations/${cid}/messages`,
      body,
    )) {
      apply(assistant, event, data);
    }
  } catch (e) {
    assistant.text +=
      (assistant.text ? "\n\n" : "") + "_Error: " + (e.message || "stream failed") + "_";
  } finally {
    assistant.streaming = false;
    chat.streaming.val = false;
    // The server materialised the conversation row on this first message; refresh
    // the sidebar so the new thread shows up without needing a page reload.
    if (isNew) conversationsSvc.list().catch(() => {});
  }
};
