// components/ui/switch.js - on/off toggle (.switch). `on` is a function for reactivity.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";

const { div } = van.tags;

export const Switch = component({
  name: "switch",
  css: `
    .switch{width:34px; height:19px; border-radius:20px; background:var(--coral-deep); position:relative; cursor:pointer; flex:none;}
    .switch::after{content:""; position:absolute; width:15px; height:15px; border-radius:50%; background:var(--on-chip); top:2px; left:17px; transition:var(--t-fast); box-shadow:var(--shadow-thumb);}
    .switch.off{background:var(--line);} .switch.off::after{left:2px;}
  `,
  // on: () => boolean (reactive); onToggle: () => void.
  view: ({ on, onToggle } = {}) =>
    div({
      class: () => "switch" + (on() ? "" : " off"),
      onclick: onToggle,
    }),
});
