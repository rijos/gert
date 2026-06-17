// state/models.js - available models + current selection. No DOM, no fetch.
import van from "/lib/van.js";
import { reactive } from "/lib/van-x.js";

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

// Tool-calling capability of the selected model. Undeclared capabilities are
// PERMISSIVE (don't cripple an unconfigured catalog) - only a model that
// declares capabilities without "tools" disables the chips. Mirrors the
// server-side gate (IModelCatalog.SupportsTools).
export const selectedSupportsTools = van.derive(() => {
  const m = selected.val;
  return !m || !m.capabilities || m.capabilities.includes("tools");
});

export const select = (id) => (selectedId.val = id);
