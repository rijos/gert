// state/knowledge.js - documents + per-doc ingest status. Keyed list via
// van-x so one doc's status pill re-renders alone. No DOM, no fetch.
// (The "Use my docs" switch is chat.tools.rag - chat-and-tools.md: flipping
// it off removes search_documents for that turn.)
import van from "van";
import { reactive } from "van-x";

export const documents = reactive([]); // [{ id, name, size, chunk_count, status, error, progress }]

export const totalBytes = van.derive(() =>
  documents.reduce((n, d) => n + (d.size || 0), 0),
);

export const setDocuments = (list) => {
  documents.length = 0;
  list.forEach((d) => documents.push(reactive(d)));
};

export const addDocument = (doc) => {
  const d = reactive(doc);
  documents.push(d);
  return d;
};

export const updateDocument = (id, patch) => {
  const d = documents.find((x) => x.id === id);
  if (d) Object.assign(d, patch);
};

export const removeDocument = (id) => {
  const i = documents.findIndex((x) => x.id === id);
  if (i >= 0) documents.splice(i, 1);
};
