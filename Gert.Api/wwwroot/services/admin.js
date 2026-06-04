// services/admin.js — admin-only user list (requires Admin policy server-side).
// GET /api/admin/users.
import * as http from "./http.js";

export const listUsers = () => http.get("/admin/users");
export const getUser = (key) => http.get(`/admin/users/${key}`);
export const removeUser = (key) => http.del(`/admin/users/${key}`);
