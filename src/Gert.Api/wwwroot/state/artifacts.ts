// state/artifacts.js - artifacts open in the canvas for the active thread.
// Keyed list via van-x so a streamed artifact opens a new tab without
// re-rendering the others.
import { reactive } from "/lib/van-x.js";

export type ArtifactKind = "md" | "html" | "svg" | "py" | "cs" | "cpp" | "js" | "rs";

export interface Artifact {
  id: string;
  kind: ArtifactKind;
  name: string;
  content: string;
  problems?: string;
}

export const artifacts = reactive<Artifact[]>([]);

export const setArtifacts = (list: Artifact[]) => {
  artifacts.length = 0;
  list.forEach((a) => artifacts.push(reactive(a)));
};

export const addArtifact = (a: Artifact): Artifact => {
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
