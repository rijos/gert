// components/settings/settings-modal.js — user settings as a centered popup
// (theme, default reply language, default model). Opened from the user chip;
// reuses the shared Modal scaffold (scrim + Cancel/Save actions).
import van from "van";
import { Modal } from "../ui/modal.js";
import { toast } from "../ui/toast.js";
import * as settingsSvc from "../../services/settings.js";
import * as models from "../../state/models.js";
import * as ui from "../../state/ui.js";

const { div, p, label, select, option, input } = van.tags;

export const openSettings = () => {
  const loaded = van.state(null);
  settingsSvc
    .get()
    .then((s) => (loaded.val = s || {}))
    .catch(() => (loaded.val = {}));

  const themeSel = select(
    {
      onchange: (e) => {
        const v = e.target.value;
        if (v === "system") {
          localStorage.removeItem("gert.theme");
          document.documentElement.removeAttribute("data-theme");
          ui.theme.val = null;
        } else {
          document.documentElement.setAttribute("data-theme", v);
          localStorage.setItem("gert.theme", v);
          ui.theme.val = v;
        }
      },
    },
    option({ value: "system" }, "Follow system"),
    option({ value: "light" }, "Light"),
    option({ value: "dark" }, "Dark"),
  );
  themeSel.value = ui.theme.val || "system";

  const body = div(
    { class: "settings-modal-body" },
    p({ class: "sub" }, "Your preferences for Gert."),
    () =>
      loaded.val === null
        ? p("Loading…")
        : div(
            div({ class: "field" }, label("Theme"), themeSel),
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
              (() => {
                const sel = select(
                  {},
                  ...models.models.map((m) => option({ value: m.id }, m.name)),
                );
                sel.value =
                  loaded.val.default_model || models.selectedId.val || "";
                sel.id = "default_model";
                return sel;
              })(),
            ),
          ),
  );

  Modal({
    title: "Settings",
    body,
    confirmLabel: "Save",
    onConfirm: () => {
      settingsSvc
        .update({
          reply_language: document.getElementById("reply_lang")?.value,
          default_model: document.getElementById("default_model")?.value,
          theme: ui.theme.val || "system",
        })
        .then(() => toast("Settings saved", "ok"))
        .catch(() => toast("Could not save settings", "err"));
    },
  });
};
