// state/tools.js - the server-driven tool catalog (GET /api/tools), the source
// for the composer's tools popup. No DOM, no fetch - services/tools.js fills it.
//
// This is the ENTITLEMENT catalog (which tools this user may use at all); the
// per-conversation on/off toggles stay in state/chat.js (`tools`). Loaded once
// at app boot like the model catalog - entitlement is a property of the token,
// not the conversation, so it never needs reloading mid-session.
import { reactive } from "/lib/van-x.js";
import type { WireToolInfo } from "../services/wire.js";

// A catalog row IS the wire shape (id, name, description, tool_type). The popup
// maps id -> a friendly client-side label; name/description are the fallback.
export type ToolInfo = WireToolInfo;

export const availableTools = reactive<ToolInfo[]>([]);

export const setAvailable = (list: ToolInfo[]) => {
  availableTools.length = 0;
  list.forEach((t) => availableTools.push(t));
};
