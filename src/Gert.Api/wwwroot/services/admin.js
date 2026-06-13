// services/admin.js - admin-only surface (requires Admin policy server-side).
// GET/DELETE /api/admin/users, GET /api/admin/system-prompt.
import * as http from "./http.js";

export const listUsers = () => http.get("/admin/users");
export const getUser = (key) => http.get(`/admin/users/${key}`);
export const removeUser = (key) => http.del(`/admin/users/${key}`);
export const getSystemPrompt = () => http.get("/admin/system-prompt");
