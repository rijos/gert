// components/settings/settings-modal.js — user settings as a centered popup
// (theme, default reply language, default model, build line). Opened from the
// user chip; reuses the shared Modal scaffold (scrim + ✕ + Cancel/Save actions)
// and the shared ui/dropdown for the theme + default-model selects.
import van from "van";
import { Modal } from "../ui/modal.js";
import { Dropdown } from "../ui/dropdown.js";
import { toast } from "../ui/toast.js";
import * as settingsSvc from "../../services/settings.js";
import * as models from "../../state/models.js";
import * as ui from "../../state/ui.js";

const { div, p, label, input } = van.tags;

const THEMES = [
  { value: "system", label: "Follow system" },
  { value: "light", label: "Light" },
  { value: "dark", label: "Dark" },
];

export const openSettings = () => {
  const loaded = van.state(null);
  const themeVal = van.state(ui.theme.val || "system");
  const modelVal = van.state("");
  settingsSvc
    .get()
    .then((s) => {
      loaded.val = s || {};
      modelVal.val = loaded.val.default_model_id || models.selectedId.val || "";
    })
    .catch(() => (loaded.val = {}));

  const applyTheme = ({ value: v }) => {
    themeVal.val = v;
    if (v === "system") {
      localStorage.removeItem("gert.theme");
      document.documentElement.removeAttribute("data-theme");
      ui.theme.val = null;
    } else {
      document.documentElement.setAttribute("data-theme", v);
      localStorage.setItem("gert.theme", v);
      ui.theme.val = v;
    }
  };

  const body = div(
    { class: "settings-modal-body" },
    p({ class: "sub" }, "Your preferences for Gert."),
    () =>
      loaded.val === null
        ? p("Loading…")
        : div(
            div(
              { class: "field" },
              label("Theme"),
              Dropdown({ items: THEMES, value: themeVal, onSelect: applyTheme }),
            ),
            div(
              { class: "field" },
              label("Default reply language"),
              input({
                value: loaded.val.reply_language || "",
                placeholder: "e.g. English",
                id: "reply_lang",
              }),
            ),
            div(
              { class: "field" },
              label("Default model"),
              Dropdown({
                items: () => models.models.map((m) => ({ value: m.id, label: m.name })),
                value: modelVal,
                placeholder: "Model",
              }),
            ),
            div({ class: "ver" }, "v0 · homelab · 20u"),
          ),
  );

  Modal({
    title: "Settings",
    closable: true,
    body,
    confirmLabel: "Save",
    onConfirm: () => {
      settingsSvc
        .update({
          // empty string = "leave unchanged" — the API treats null/absent as no-op
          reply_language: document.getElementById("reply_lang")?.value || undefined,
          default_model_id: modelVal.val || undefined,
          theme: ui.theme.val || "auto", // wire enum: light | dark | auto
        })
        .then(() => toast("Settings saved", "ok"))
        .catch(() => toast("Could not save settings", "err"));
    },
  });
};
