// components/settings/settings-modal.js - user settings as a centered popup
// (theme, send chord, default reply language, default model, memory mode,
// build line). Opened from the user chip; reuses the shared Modal scaffold
// (scrim + x + Cancel/Save actions), ui/dropdown and ui/seg-toggle. Sampling
// rides the selected chat provider (the picker lists provider presets), not a
// per-user dial, so there is no per-model settings modal here.
import van from "/lib/van.js";
import { Modal } from "../ui/modal.js";
import { Dropdown } from "../ui/dropdown.js";
import { SegToggle } from "../ui/seg-toggle.js";
import { toast } from "../ui/toast.js";
import * as settingsSvc from "../../services/settings.js";
import * as models from "../../state/models.js";
import * as ui from "../../state/ui.js";
import { t, lang, setLang, AVAILABLE } from "../../lib/i18n.js";

const { div, p, label, input } = van.tags;

const THEMES = [
  { value: "system", label: t("Follow system") },
  { value: "manila", label: t("Manila (paper)") },
  { value: "ember", label: t("Ember (dark)") },
];

const MEMORY_MODES = [
  { value: "off", label: t("Off - never store memories") },
  { value: "manual", label: t("Manual - only when I ask") },
  { value: "auto", label: t("Automatic - the model decides") },
];

export const openSettings = () => {
  const loaded = van.state(null);
  const themeVal = van.state(ui.theme.val || "system");
  const modelVal = van.state("");
  const submitVal = van.state(ui.submitKey.val);
  const memoryVal = van.state("manual");
  // derived from the browser on first load (lib/i18n.js); explicit choice wins
  const langVal = van.state(lang());

  settingsSvc
    .get()
    .then((s) => {
      loaded.val = s || {};
      modelVal.val = loaded.val.default_model_id || models.selectedId.val || "";
      memoryVal.val = loaded.val.memory_mode || "manual";
    })
    .catch(() => (loaded.val = {}));

  // state/ui.js owns [data-theme] + the gert.theme key - delegate, never touch.
  const applyTheme = ({ value: v }) => {
    themeVal.val = v;
    ui.setTheme(v === "system" ? null : v);
  };

  let replyLangEl = null; // closure-held element ref - no getElementById

  const body = div(
    { class: "settings-modal-body" },
    p({ class: "sub" }, t("Your preferences for Gert.")),
    () => {
      if (loaded.val === null) return p("Loading...");
      replyLangEl = input({
        value: loaded.val.reply_language || "",
        placeholder: t("e.g. English"),
      });
      return div(
        div(
          { class: "field" },
          label(t("Theme")),
          Dropdown({ items: THEMES, value: themeVal, onSelect: applyTheme }),
        ),
        div(
          { class: "field" },
          label(t("Language")),
          Dropdown({ items: AVAILABLE, value: langVal }),
        ),
        div(
          { class: "field" },
          label(t("Send with")),
          // applies immediately (a device preference, not part of Save)
          SegToggle({
            options: [
              { value: "enter", label: "Enter" },
              { value: "mod_enter", label: "Ctrl/Cmd + Enter" },
            ],
            value: () => submitVal.val,
            onSelect: (v) => {
              submitVal.val = v;
              ui.setSubmitKey(v);
            },
          }),
        ),
        div(
          { class: "field" },
          label(t("Default reply language")),
          replyLangEl,
        ),
        div(
          { class: "field" },
          label(t("Memories")),
          Dropdown({ items: MEMORY_MODES, value: memoryVal }),
        ),
        div(
          { class: "field" },
          label(t("Default model")),
          Dropdown({
            items: () => models.models.map((m) => ({ value: m.id, label: m.name })),
            value: modelVal,
            placeholder: "Model",
            // onSelect replaces the default set - keep value.val in sync too
            onSelect: ({ value: v }) => {
              modelVal.val = v;
            },
          }),
        ),
        p(
          { class: "sub" },
          t("Generation dials (temperature, top-p, ...) live in the model picker - the cogwheel on each model."),
        ),
        div({ class: "ver" }, "v1.0 - homelab"),
      );
    },
  );

  Modal({
    title: t("Settings"),
    closable: true,
    body,
    confirmLabel: t("Save"),
    onConfirm: () => {
      settingsSvc
        .update({
          // empty string = "leave unchanged" - the API treats null/absent as no-op
          reply_language: replyLangEl?.value || undefined,
          default_model_id: modelVal.val || undefined,
          memory_mode: memoryVal.val,
          ui_language: langVal.val,
          // wire enum is light | dark | auto (configuration.md section 3.1), not the
          // theme names - map back from manila/ember.
          theme: { manila: "light", ember: "dark" }[ui.theme.val] || "auto",
        })
        .then(() => {
          toast(t("Settings saved"), "ok");
          // a changed language reloads the page (strings render once per load)
          setLang(langVal.val);
        })
        .catch(() => toast(t("Could not save settings"), "err"));
    },
  });
};
