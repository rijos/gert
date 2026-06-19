// services/settings.js - GET/PUT /api/settings (user-level preferences).
import * as http from "./http.js";
import type { WireSettings } from "./wire.js";

export const get = () => http.get<WireSettings>("/settings");
export const update = (patch: WireSettings) => http.put<WireSettings>("/settings", patch);
