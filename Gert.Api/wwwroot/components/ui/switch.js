// components/ui/switch.js — on/off toggle (.switch). `on` is a function for reactivity.
import van from "van";

const { div } = van.tags;

// on: () => boolean (reactive); onToggle: () => void.
export const Switch = ({ on, onToggle } = {}) =>
  div({
    class: () => "switch" + (on() ? "" : " off"),
    onclick: onToggle,
  });
