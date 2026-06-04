// state/models.js — available models + current selection. No DOM, no fetch.
import van from "van";
import { reactive } from "van-x";

export const models = reactive([]); // [{ id, name, default, capabilities, context, fast }]
export const selectedId = van.state(null);

export const setModels = (list) => {
  models.length = 0;
  list.forEach((m) => models.push(m));
  if (!selectedId.val) {
    const def = list.find((m) => m.default) || list[0];
    if (def) selectedId.val = def.id;
  }
};

export const selected = van.derive(() =>
  models.find((m) => m.id === selectedId.val) || models[0] || null,
);

export const select = (id) => (selectedId.val = id);
