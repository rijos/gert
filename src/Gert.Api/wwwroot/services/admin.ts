// Admin-only surface under /api/admin (requires the Admin policy server-side).
import * as http from "./http.js";
import type { WireSystemPrompt, WireUserSummary } from "./wire.js";

export const listUsers = () => http.get<WireUserSummary[]>("/admin/users");
export const getUser = (key: string) => http.get<WireUserSummary>(`/admin/users/${key}`);
export const removeUser = (key: string) => http.del(`/admin/users/${key}`);
export const getSystemPrompt = () => http.get<WireSystemPrompt>("/admin/system-prompt");
