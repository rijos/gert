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
const NumField = (labelText, inputEl) =>
  div(
    { class: "field" },
    label(labelText),
    inputEl,
    );

const parsed = (el) => {
  const raw = el?.value?.trim();
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

  // closure-held element refs (house pattern — no getElementById)
  const els = {};

  const body = div(
    { class: "settings-modal-body" },
    p({ class: "sub" }, "Generation defaults for this model. Blank = inherit."),
    () => {
      if (loaded.val === null) return p("Loading…");
      els.temperature = input({
        type: "number",
        placeholder: "0 – 2",
        min: 0,
        max: 2,
        step: 0.05,
        value: loaded.val.temperature ?? "",
      });
      els.topP = input({
        type: "number",
        placeholder: "0 – 1",
        min: 0,
        max: 1,
        step: 0.01,
        value: loaded.val.top_p ?? "",
      });
      els.maxTokens = input({
        type: "number",
        placeholder: "e.g. 4096",
        min: 1,
        step: 1,
        value: loaded.val.max_tokens ?? "",
      });
      return div(
        NumField("Temperature", els.temperature),
        NumField("Top P", els.topP),
        NumField("Max tokens", els.maxTokens),
      );
    },
  );

  Modal({
    title: `${model.name || model.id} settings`,
    closable: true,
    body,
    confirmLabel: "Save",
    onConfirm: () => {
      const maxTokens = parsed(els.maxTokens);
      settingsSvc
        .update({
          model_params: {
            // whole-entry replace for this model id; blanks = inherit
            [model.id]: {
              temperature: parsed(els.temperature),
              top_p: parsed(els.topP),
              max_tokens: maxTokens != null ? Math.round(maxTokens) : undefined,
            },
          },
        })
        .then(() => toast("Model settings saved", "ok"))
        .catch(() => toast("Could not save model settings", "err"));
    },
  });
};
