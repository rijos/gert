// services/models.js — GET /api/models → state/models.js.
import * as http from "./http.js";
import * as models from "../state/models.js";
import * as settings from "./settings.js";

export const load = async () => {
  const list = await http.get("/models");
  models.setModels(list || []);
  return list;
};

// Boot-time load: the user's saved default model (settings.json) wins over the
// catalog-flagged default — the configuration cascade, nearest level wins.
export const loadWithUserDefault = async () => {
  const [list, prefs] = await Promise.all([
    load(),
    settings.get().catch(() => null),
  ]);
  const id = prefs?.default_model_id;
  if (id && (list || []).some((m) => m.id === id)) models.select(id);
  return list;
};
