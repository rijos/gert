// services/projects.js — list / create / select / delete projects.
// Not in the §6 service list of ui-components.md but required by the project
// picker (configuration §8). Updates state/chat.js.
import * as http from "./http.js";
import * as chat from "../state/chat.js";
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

export const select = async (id) => {
  chat.activeProjectId.val = id;
  chat.newConversation();
  await conversations.list();
};

export const remove = async (id) => {
  await http.del(`/projects/${id}`);
  await list();
  if (chat.activeProjectId.val === id) await select("default");
};
