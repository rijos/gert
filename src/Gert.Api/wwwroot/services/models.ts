// services/models.js - GET /api/models -> state/models.js.
import * as http from "./http.js";
import * as models from "../state/models.js";
import type { WireModel } from "./wire.js";
import * as settings from "./settings.js";
import { applyServerLanguage } from "../lib/i18n.js";

export const load = async () => {
  const list = await http.get<WireModel[]>("/models");
  models.setModels(list);
  return list;
};

// Boot-time load: the user's saved default model (settings.json) wins over the
// catalog-flagged default - the configuration cascade, nearest level wins.
export const loadWithUserDefault = async () => {
  const [list, prefs] = await Promise.all([
    load(),
    settings.get().catch(() => null),
  ]);
  const id = prefs?.default_model_id;
  if (id && list.some((m) => m.id === id)) models.select(id);
  // Theme is device-local (localStorage); restoreTheme() at boot is the only source - no
  // server reconciliation. UI language still cascades from server settings.
  if (prefs) applyServerLanguage(prefs.ui_language);
  return list;
};
