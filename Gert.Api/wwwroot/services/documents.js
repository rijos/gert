// services/documents.js — upload, poll status, delete documents.
// Project-scoped: /api/projects/{pid}/documents. Updates state/knowledge.js.
import * as http from "./http.js";
import * as knowledge from "../state/knowledge.js";
import * as chat from "../state/chat.js";

const pid = () => chat.activeProjectId.val;

export const list = async () => {
  const items = await http.get(`/projects/${pid()}/documents`);
  knowledge.setDocuments(items || []);
  return items;
};

// upload a File; insert the optimistic "processing" row, then poll to completion.
export const upload = async (file) => {
  const fd = new FormData();
  fd.append("file", file, file.name);
  const created = await http.upload(`/projects/${pid()}/documents`, fd);
  const doc = knowledge.addDocument({
    id: created.id,
    name: created.name || file.name,
    size: created.size ?? file.size,
    chunk_count: created.chunk_count ?? 0,
    status: created.status || "processing",
    error: null,
  });
  poll(doc.id);
  return doc;
};

// poll one document until it leaves "processing".
export const poll = async (id) => {
  const project = pid();
  while (true) {
    let d;
    try {
      d = await http.get(`/projects/${project}/documents/${id}`);
    } catch {
      return;
    }
    knowledge.updateDocument(id, {
      status: d.status,
      chunk_count: d.chunk_count,
      progress: d.progress,
      error: d.error,
    });
    if (d.status !== "processing") return;
    await new Promise((r) => setTimeout(r, 1500));
  }
};

export const remove = async (id) => {
  await http.del(`/projects/${pid()}/documents/${id}`);
  knowledge.removeDocument(id);
};
