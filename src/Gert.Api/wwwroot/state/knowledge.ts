// state/knowledge.js - documents + per-doc ingest status. Keyed list via
// van-x so one doc's status pill re-renders alone. No DOM, no fetch.
// (The "Use my docs" switch is chat.tools.rag - chat-and-tools.md: flipping
// it off removes search_documents for that turn.)
import van from "/lib/van.js";
import { reactive } from "/lib/van-x.js";

// [{ id, name, size, chunk_count, status, error, progress }] - the WireDocument subset the
// store keeps. `error` is nullable on the wire (null unless status is "failed").
export interface Document {
  id: string;
  name: string;
  size?: number;
  chunk_count?: number;
  status?: string;
  error?: string | null;
  progress?: number;
}

export const documents = reactive<Document[]>([]);

export const totalBytes = van.derive(() =>
  documents.reduce((n, d) => n + (d.size || 0), 0),
);

export const setDocuments = (list: Document[]) => {
  documents.length = 0;
  list.forEach((d) => documents.push(reactive(d)));
};

export const addDocument = (doc: Document): Document => {
  const d = reactive(doc);
  documents.push(d);
  return d;
};

// A status patch from the ingest poll: each field may be absent OR explicitly undefined (the
// poll response omits what it does not know yet), so the value types include `undefined` -
// Partial<Document> alone would reject an explicit undefined under exactOptionalPropertyTypes.
export type DocumentPatch = { [K in keyof Document]?: Document[K] | undefined };

export const updateDocument = (id: string, patch: DocumentPatch) => {
  const d = documents.find((x) => x.id === id);
  if (d) Object.assign(d, patch);
};

export const removeDocument = (id: string) => {
  const i = documents.findIndex((x) => x.id === id);
  if (i >= 0) documents.splice(i, 1);
};
