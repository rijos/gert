// services/admin.js - admin-only surface (requires Admin policy server-side).
// GET/DELETE /api/admin/users, GET /api/admin/system-prompt.
import * as http from "./http.js";
import type { WireSystemPrompt, WireUserSummary } from "./wire.js";

export const listUsers = () => http.get<WireUserSummary[]>("/admin/users");
export const getUser = (key: string) => http.get<WireUserSummary>(`/admin/users/${key}`);
export const removeUser = (key: string) => http.del(`/admin/users/${key}`);
export const getSystemPrompt = () => http.get<WireSystemPrompt>("/admin/system-prompt");
