// services/projects.js - list / create / select / delete projects.
// Not in the section 6 service list of ui-components.md but required by the project
// picker (configuration section 8). Updates state/chat.js.
import * as http from "./http.js";
import * as chat from "../state/chat.js";
import type { Conversation } from "../state/chat.js";
import type { WireProject, WireProjectInput } from "./wire.js";
import * as chatSvc from "./chat.js";
import * as conversations from "./conversations.js";

export const list = async () => {
  // GET projects returns the project picker rows (WireProject); they drop straight into the
  // project store (a Project is the {id,name} subset).
  const items = await http.get<WireProject[]>("/projects");
  chat.setProjects(items);
  return items;
};

export const create = async (body: WireProjectInput) => {
  const p = await http.post<WireProject>("/projects", body);
  await list();
  return p;
};

// Switch project, load its conversations, and open the most recently-updated one
// (so you land on your latest topic, not a blank composer). Falls back to a fresh
// conversation when the project is empty. Returns the opened conversation (or null)
// so the caller can route to it.
export const select = async (id: string) => {
  chatSvc.detach(); // leaving a mid-stream thread - unpin the composer
  chat.activeProjectId.val = id;
  chat.newConversation();
  const items = (await conversations.list()) || [];
  const recent = items.reduce<Conversation | null>(
    (best, c) =>
      !best || new Date(c.updated_at || 0) > new Date(best.updated_at || 0) ? c : best,
    null,
  );
  if (recent) await conversations.open(recent.id);
  return recent;
};

export const rename = async (id: string, name: string) => {
  await http.patch(`/projects/${id}`, { name });
  await list();
};

export const remove = async (id: string) => {
  await http.del(`/projects/${id}`);
  await list();
  if (chat.activeProjectId.val === id) await select("default");
};
