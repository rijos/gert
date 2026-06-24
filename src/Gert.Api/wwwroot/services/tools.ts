// services/tools.js - GET /api/tools -> state/tools.js. The server-driven tool
// catalog the composer's popup renders: the registered tools filtered to what
// this user's gert_tools claim entitles (the same ceiling the turn planner
// applies). Mirrors services/models.js.
import * as http from "./http.js";
import * as tools from "../state/tools.js";
import * as chat from "../state/chat.js";
import type { WireToolInfo } from "./wire.js";

export const load = async () => {
  const list = await http.get<WireToolInfo[]>("/tools");
  tools.setAvailable(list);
  // Default-enable derives from the catalog: every entitled tool starts on (entitlement is the
  // real gate). Non-destructive, so a toggle restored from a conversation later wins.
  chat.seedToolDefaults(list.map((t) => t.id));
  return list;
};
