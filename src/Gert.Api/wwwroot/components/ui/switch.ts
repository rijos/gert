// components/ui/switch.js - on/off toggle (.switch). `on` is a function for reactivity.
// A real <button role="switch">: focusable + Enter/Space-operable for free (WCAG 2.1.1),
// with aria-checked exposing state (4.1.2). Pass `label` when the switch has no adjacent
// <label>/text so it still has an accessible name.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";

const { button } = van.tags;

interface SwitchProps {
  on: () => boolean;
  onToggle: () => void;
  label?: string;
}

export const Switch = component({
  name: "switch",
  css: `
    .switch {
      appearance: none;
      width: 34px;
      height: 19px;
      border: none;
      padding: 0;
      border-radius: 20px;
      background: var(--coral-deep);
      position: relative;
      cursor: pointer;
      flex: none;
    }

    .switch::after {
      content: "";
      position: absolute;
      width: 15px;
      height: 15px;
      border-radius: 50%;
      background: var(--on-chip);
      top: 2px;
      left: 17px;
      transition: var(--t-fast);
      box-shadow: var(--shadow-thumb);
    }

    .switch.off {
      background: var(--line);
    }
    .switch.off::after {
      left: 2px;
    }
  `,
  // `= {} as SwitchProps` keeps the no-arg default (byte-identical emit) while
  // typing the always-passed fields as required so `on()` type-checks.
  view: ({ on, onToggle, label }: SwitchProps = {} as SwitchProps) =>
    button({
      type: "button",
      role: "switch",
      class: () => "switch" + (on() ? "" : " off"),
      "aria-checked": () => String(on()),
      ...(label ? { "aria-label": label } : {}),
      onclick: onToggle,
    }),
});
