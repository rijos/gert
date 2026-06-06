// components/settings/model-settings-modal.js — per-model generation settings
// (the picker's cogwheel): temperature / top-p / max tokens stored in user
// settings as model_params[modelId]. Blank = inherit the server defaults.
// Mirrors settings-modal.js (shared Modal scaffold + toast).
import van from "van";
import { Modal } from "../ui/modal.js";
import { toast } from "../ui/toast.js";
import * as settingsSvc from "../../services/settings.js";

const { div, p, label, input } = van.tags;

// number-input field; empty string round-trips as "inherit"
const NumField = (labelText, hint, attrs) =>
  div(
    { class: "field" },
    label(labelText),
    input({ type: "number", placeholder: hint, ...attrs }),
    );

const parsed = (id) => {
  const raw = document.getElementById(id)?.value?.trim();
  if (!raw) return undefined;
  const n = Number(raw);
  return Number.isFinite(n) ? n : undefined;
};

export const openModelSettings = (model) => {
  const loaded = van.state(null);
  settingsSvc
    .get()
    .then((s) => (loaded.val = s?.model_params?.[model.id] || {}))
    .catch(() => (loaded.val = {}));

  const body = div(
    { class: "settings-modal-body" },
    p({ class: "sub" }, "Generation defaults for this model. Blank = inherit."),
    () =>
      loaded.val === null
        ? p("Loading…")
        : div(
            NumField("Temperature", "0 – 2", {
              id: "mp_temperature",
              min: 0,
              max: 2,
              step: 0.05,
              value: loaded.val.temperature ?? "",
            }),
            NumField("Top P", "0 – 1", {
              id: "mp_top_p",
              min: 0,
              max: 1,
              step: 0.01,
              value: loaded.val.top_p ?? "",
            }),
            NumField("Max tokens", "e.g. 4096", {
              id: "mp_max_tokens",
              min: 1,
              step: 1,
              value: loaded.val.max_tokens ?? "",
            }),
          ),
  );

  Modal({
    title: `${model.name || model.id} settings`,
    closable: true,
    body,
    confirmLabel: "Save",
    onConfirm: () => {
      const maxTokens = parsed("mp_max_tokens");
      settingsSvc
        .update({
          model_params: {
            // whole-entry replace for this model id; blanks = inherit
            [model.id]: {
              temperature: parsed("mp_temperature"),
              top_p: parsed("mp_top_p"),
              max_tokens: maxTokens != null ? Math.round(maxTokens) : undefined,
            },
          },
        })
        .then(() => toast("Model settings saved", "ok"))
        .catch(() => toast("Could not save model settings", "err"));
    },
  });
};
