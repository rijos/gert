// services/conversations.js — list / create / rename / delete conversations.
// Project-scoped: /api/projects/{pid}/conversations. Updates state/chat.js.
import * as http from "./http.js";
import * as chat from "../state/chat.js";
import * as artifacts from "../state/artifacts.js";

const pid = () => chat.activeProjectId.val;

export const list = async () => {
  const items = await http.get(`/projects/${pid()}/conversations`);
  chat.setConversations(items || []);
  return items;
};

export const open = async (id) => {
  // GET thread returns the flattened contract: { id, title, tools, messages:[{ id,
  // role, text, citations }], artifacts } — consumed directly, no remapping.
  const conv = await http.get(`/projects/${pid()}/conversations/${id}`);
  chat.setConversation(conv);
  artifacts.setArtifacts(conv.artifacts || []);
  // `tools` is the ToolToggles map { rag, search, sandbox }.
  const t = conv.tools;
  if (t && typeof t === "object") {
    chat.tools.rag = !!t.rag;
    chat.tools.search = !!t.search;
    chat.tools.sandbox = !!t.sandbox;
  }
  return conv;
};

export const create = async (body = {}) => {
  const conv = await http.post(`/projects/${pid()}/conversations`, body);
  await list();
  chat.setConversation(conv);
  return conv;
};

export const rename = async (id, title) => {
  await http.patch(`/projects/${pid()}/conversations/${id}`, { title });
  const c = chat.conversations.find((x) => x.id === id);
  if (c) c.title = title;
  if (chat.activeId.val === id) chat.setTitle(title);
};

export const remove = async (id) => {
  await http.del(`/projects/${pid()}/conversations/${id}`);
  const i = chat.conversations.findIndex((x) => x.id === id);
  if (i >= 0) chat.conversations.splice(i, 1);
  if (chat.activeId.val === id) chat.newConversation();
};
