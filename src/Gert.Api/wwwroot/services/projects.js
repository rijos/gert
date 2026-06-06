// services/projects.js — list / create / select / delete projects.
// Not in the §6 service list of ui-components.md but required by the project
// picker (configuration §8). Updates state/chat.js.
import * as http from "./http.js";
import * as chat from "../state/chat.js";
import * as chatSvc from "./chat.js";
import * as conversations from "./conversations.js";

export const list = async () => {
  const items = await http.get("/projects");
  chat.setProjects(items || []);
  return items;
};

export const create = async (body) => {
  const p = await http.post("/projects", body);
  await list();
  return p;
};

// Switch project, load its conversations, and open the most recently-updated one
// (so you land on your latest topic, not a blank composer). Falls back to a fresh
// conversation when the project is empty. Returns the opened conversation (or null)
// so the caller can route to it.
export const select = async (id) => {
  chatSvc.detach(); // leaving a mid-stream thread — unpin the composer
  chat.activeProjectId.val = id;
  chat.newConversation();
  const items = (await conversations.list()) || [];
  const recent = items.reduce(
    (best, c) =>
      !best || new Date(c.updated_at || 0) > new Date(best.updated_at || 0) ? c : best,
    null,
  );
  if (recent) await conversations.open(recent.id);
  return recent;
};

export const remove = async (id) => {
  await http.del(`/projects/${id}`);
  await list();
  if (chat.activeProjectId.val === id) await select("default");
};
