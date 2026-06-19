// Per-project memory entries under /api/projects/{pid}/memory.
import * as http from "./http.js";
import * as chat from "../state/chat.js";
import type { WireMemoryEntry, WireMemoryInput } from "./wire.js";

const pid = () => chat.activeProjectId.val;

export const list = () => http.get<WireMemoryEntry[]>(`/projects/${pid()}/memory`);
export const add = (entry: WireMemoryInput) => http.post(`/projects/${pid()}/memory`, entry);
export const remove = (id: string) => http.del(`/projects/${pid()}/memory/${id}`);
