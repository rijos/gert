// services/settings.js - GET/PUT /api/settings (user-level preferences).
import * as http from "./http.js";

export const get = () => http.get("/settings");
export const update = (patch) => http.put("/settings", patch);
