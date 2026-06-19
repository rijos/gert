// services/documents.js - upload, poll status, delete documents.
// Project-scoped: /api/projects/{pid}/documents. Updates state/knowledge.js.
import * as http from "./http.js";
import * as knowledge from "../state/knowledge.js";
import type { WireDocument } from "./wire.js";
import * as chat from "../state/chat.js";

const pid = () => chat.activeProjectId.val;

export const list = async () => {
  // GET documents returns WireDocument rows - the Document store rows.
  const items = await http.get<WireDocument[]>(`/projects/${pid()}/documents`);
  knowledge.setDocuments(items);
  return items;
};

// upload a File; insert the optimistic "processing" row, then poll to completion.
// The created document already matches the doc contract, so it drops straight in.
export const upload = async (file: File) => {
  const fd = new FormData();
  fd.append("file", file, file.name);
  const created = await http.upload<WireDocument>(`/projects/${pid()}/documents`, fd);
  const doc = knowledge.addDocument(created);
  poll(doc.id);
  return doc;
};

// poll one document until it leaves "processing".
export const poll = async (id: string) => {
  const project = pid();
  while (true) {
    let d: WireDocument;
    try {
      d = await http.get<WireDocument>(`/projects/${project}/documents/${id}`);
    } catch {
      return;
    }
    // Copy the ingest-status fields through (any may be absent), exactly as the JS original did.
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

export const remove = async (id: string) => {
  await http.del(`/projects/${pid()}/documents/${id}`);
  knowledge.removeDocument(id);
};
