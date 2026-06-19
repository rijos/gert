// components/ui/button.js - primary/secondary action button.
// `.btn` is a shared utility in primitives.css (applied by bare class-string across
// the app), so this component owns no css of its own.
import van from "/lib/van.js";
import type { PropValueOrDerived } from "/lib/van.js";
import { component } from "../../lib/component.js";

const { button } = van.tags;

interface ButtonProps {
  label?: string;
  onclick?: (e: Event) => void;
  variant?: string;
  type?: string;
  disabled?: boolean | (() => boolean);
}

export const Button = component({
  name: "button",
  view: ({ label, onclick, variant = "primary", type = "button", disabled }: ButtonProps = {}) =>
    button(
      // van's prop type rejects `undefined` (only `null` is a no-op value), but the
      // runtime passes either through unchanged; these casts pass the optional
      // props on verbatim without an exactOptionalPropertyTypes-driven omission.
      { class: "btn" + (variant === "secondary" ? " secondary" : ""), type, onclick: onclick as PropValueOrDerived, disabled: disabled as PropValueOrDerived },
      label,
    ),
});
