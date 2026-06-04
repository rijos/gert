// components/ui/button.js — primary/secondary action button.
import van from "van";

const { button } = van.tags;

export const Button = ({ label, onclick, variant = "primary", type = "button", disabled } = {}) =>
  button(
    {
      class: "btn" + (variant === "secondary" ? " secondary" : ""),
      type,
      onclick,
      disabled,
    },
    label,
  );
