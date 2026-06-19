// services/models.js - GET /api/models -> state/models.js.
import * as http from "./http.js";
import * as models from "../state/models.js";
import type { WireModel } from "./wire.js";
import * as ui from "../state/ui.js";
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
  // The server-side theme is the cross-device truth (configuration.md section 3.1);
  // the localStorage copy restoreTheme() applied was only a first-paint cache.
  if (prefs) ui.applyServerTheme(prefs.theme ?? "");
  // same cascade for the UI language (ui_language) - cached for the next load
  if (prefs) applyServerLanguage(prefs.ui_language);
  return list;
};
