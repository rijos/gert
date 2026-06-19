// state/models.js - available models + current selection. No DOM, no fetch.
import van from "/lib/van.js";
import { reactive } from "/lib/van-x.js";

// [{ id, name, default, capabilities, context, fast }] - the WireModel subset the store keeps.
// capabilities/context are nullable on the wire (null = undeclared); keep them so a catalog row
// drops in without a cast (undeclared capabilities are treated permissively below).
export interface Model {
  id: string;
  name: string;
  default?: boolean;
  capabilities?: string[] | null;
  context?: number | null;
  fast?: boolean;
}

export const models = reactive<Model[]>([]);
export const selectedId = van.state<string | null>(null);

export const setModels = (list: Model[]) => {
  models.length = 0;
  list.forEach((m) => models.push(m));
  if (!selectedId.val) {
    // list[0] guarded by find-or-fallback: `def` may be undefined for an empty list.
    const def = list.find((m) => m.default) || list[0];
    if (def) selectedId.val = def.id;
  }
};

export const selected = van.derive<Model | null>(() =>
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

export const select = (id: string) => (selectedId.val = id);
