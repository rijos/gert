// components/settings/settings-modal.js - user settings popup (theme, send chord,
// reply language, default model, build line) over the shared Modal
// scaffold. Sampling rides the selected chat provider (the picker lists provider
// presets), not a per-user dial, so there is no per-model settings modal here.
import van from "/lib/van.js";
import { Modal } from "../ui/modal.js";
import { Dropdown } from "../ui/dropdown.js";
import { SegToggle } from "../ui/seg-toggle.js";
import { toast } from "../ui/toast.js";
import * as settingsSvc from "../../services/settings.js";
import type { WireSettings } from "../../services/wire.js";
import * as models from "../../state/models.js";
import * as ui from "../../state/ui.js";
import { t, lang, setLang, AVAILABLE } from "../../lib/i18n.js";

const { div, p, label, input } = van.tags;

// The user-settings row off GET /settings is WireSettings (configuration.md section 3): every
// field is optional - an unset preference is absent. `{}` (no settings yet) and a fetch failure
// both collapse to an empty WireSettings.

interface Option {
  value: string;
  label: string;
}

const THEMES = [
  { value: "system", label: t("Follow system") },
  { value: "manila", label: t("Manila (paper)") },
  { value: "ember", label: t("Ember (dark)") },
  { value: "frost", label: t("Frost (cool light)") },
  { value: "slate", label: t("Slate (cool dark)") },
  { value: "midnight", label: t("Midnight (dark)") },
  { value: "nord", label: t("Nord (dark)") },
  { value: "onyx", label: t("Onyx (dark)") },
  { value: "forest", label: t("Forest (dark)") },
];

export const openSettings = () => {
  const loaded = van.state<WireSettings | null>(null);
  const themeVal = van.state(ui.theme.val || "system");
  const modelVal = van.state("");
  // widened to string: the seg-toggle hands back the clicked option's value as a
  // bare string, and ui.setSubmitKey re-narrows it to the SubmitKey union.
  const submitVal = van.state<string>(ui.submitKey.val);
  // derived from the browser on first load (lib/i18n.js); explicit choice wins
  const langVal = van.state(lang());

  settingsSvc
    .get()
    .then((s) => {
      const data = s || {};
      loaded.val = data;
      modelVal.val = data.default_model_id || models.selectedId.val || "";
    })
    .catch(() => (loaded.val = {}));

  // state/ui.js owns [data-theme] + the gert.theme key - delegate, never touch.
  const applyTheme = ({ value: v }: Option) => {
    themeVal.val = v;
    ui.setTheme(v === "system" ? null : v);
  };

  let replyLangEl: HTMLInputElement | null = null; // closure-held element ref - no getElementById

  const body = div(
    { class: "settings-modal-body" },
    p({ class: "sub" }, t("Your preferences for Gert.")),
    () => {
      if (loaded.val === null) return p("Loading...");
      replyLangEl = input({
        id: "set-reply-language",
        value: loaded.val.reply_language || "",
        placeholder: t("e.g. English"),
      });
      return div(
        div(
          { class: "field" },
          label(t("Theme")),
          Dropdown({ items: THEMES, value: themeVal, onSelect: applyTheme, ariaLabel: t("Theme") }),
        ),
        div(
          { class: "field" },
          label(t("Language")),
          Dropdown({ items: AVAILABLE, value: langVal, ariaLabel: t("Language") }),
        ),
        div(
          { class: "field" },
          label(t("Send with")),
          // applies immediately (a device preference, not part of Save)
          SegToggle({
            label: t("Send with"),
            options: [
              { value: "enter", label: "Enter" },
              { value: "mod_enter", label: "Ctrl/Cmd + Enter" },
            ],
            value: () => submitVal.val,
            onSelect: (v: string) => {
              submitVal.val = v;
              ui.setSubmitKey(v);
            },
          }),
        ),
        div(
          { class: "field" },
          label({ for: "set-reply-language" }, t("Default reply language")),
          replyLangEl,
        ),
        div(
          { class: "field" },
          label(t("Default model")),
          Dropdown({
            items: () => models.models.map((m) => ({ value: m.id, label: m.name })),
            value: modelVal,
            placeholder: "Model",
            ariaLabel: t("Default model"),
            // onSelect replaces the default set - keep value.val in sync too
            onSelect: ({ value: v }: Option) => {
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
          // empty string = "leave unchanged": omit the key entirely (the API treats absent as a
          // no-op, and exactOptionalPropertyTypes forbids an explicit `undefined` on the field).
          ...(replyLangEl?.value ? { reply_language: replyLangEl.value } : {}),
          ...(modelVal.val ? { default_model_id: modelVal.val } : {}),
          ui_language: langVal.val,
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
