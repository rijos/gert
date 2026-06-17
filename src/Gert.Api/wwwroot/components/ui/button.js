// components/ui/button.js - primary/secondary action button.
// `.btn` is a shared utility in primitives.css (applied by bare class-string across
// the app), so this component owns no css of its own.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";

const { button } = van.tags;

export const Button = component({
  name: "button",
  view: ({ label, onclick, variant = "primary", type = "button", disabled } = {}) =>
    button(
      {
        class: "btn" + (variant === "secondary" ? " secondary" : ""),
        type,
        onclick,
        disabled,
      },
      label,
    ),
});
