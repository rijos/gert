// state/artifacts.js — artifacts open in the canvas for the active thread.
// Keyed list via van-x so a streamed artifact opens a new tab without
// re-rendering the others. No DOM, no fetch.
import { reactive } from "van-x";

export const artifacts = reactive([]); // [{ id, kind: "md"|"html"|"svg"|"py"|"cs"|"cpp"|"js"|"rs", name, content, problems? }]

export const setArtifacts = (list) => {
  artifacts.length = 0;
  list.forEach((a) => artifacts.push(reactive(a)));
};

export const addArtifact = (a) => {
  const existing = artifacts.find((x) => x.id === a.id);
  if (existing) {
    Object.assign(existing, a);
    return existing;
  }
  const node = reactive(a);
  artifacts.push(node);
  return node;
};

export const clear = () => (artifacts.length = 0);

export const byId = (id) => artifacts.find((a) => a.id === id) || null;
