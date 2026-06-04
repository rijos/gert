// services/models.js — GET /api/models → state/models.js.
import * as http from "./http.js";
import * as models from "../state/models.js";

export const load = async () => {
  const list = await http.get("/models");
  models.setModels(list || []);
  return list;
};
