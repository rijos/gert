// pages/settings.js — /settings — user settings (theme, language, defaults,
// memory mode). Loads via services/settings; saves on change.
import van from "van";
import { Button } from "../components/ui/button.js";
import { Switch } from "../components/ui/switch.js";
import { toast } from "../components/ui/toast.js";
import * as settingsSvc from "../services/settings.js";
import * as models from "../state/models.js";
import * as ui from "../state/ui.js";

const { div, h1, p, label, select, option, input } = van.tags;

export const SettingsPage = () => {
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

  return div(
    { class: "page" },
    div(
      { class: "page-inner" },
      h1("Settings"),
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
                    ...models.models.map((m) =>
                      option({ value: m.id }, m.name),
                    ),
                  );
                  sel.value = loaded.val.default_model || models.selectedId.val || "";
                  sel.id = "default_model";
                  return sel;
                })(),
              ),
              Button({
                label: "Save",
                onclick: () => {
                  settingsSvc
                    .update({
                      reply_language: document.getElementById("reply_lang")?.value,
                      default_model: document.getElementById("default_model")?.value,
                      theme: ui.theme.val || "system",
                    })
                    .then(() => toast("Settings saved", "ok"))
                    .catch(() => toast("Could not save settings", "err"));
                },
              }),
            ),
    ),
  );
};
