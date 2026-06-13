// services/memory.js - per-project memory entries.
// /api/projects/{pid}/memory.
import * as http from "./http.js";
import * as chat from "../state/chat.js";

const pid = () => chat.activeProjectId.val;

export const list = () => http.get(`/projects/${pid()}/memory`);
export const add = (entry) => http.post(`/projects/${pid()}/memory`, entry);
export const remove = (id) => http.del(`/projects/${pid()}/memory/${id}`);
