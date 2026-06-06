// components/ui/switch.js — on/off toggle (.switch). `on` is a function for reactivity.
import van from "van";
import { component } from "../../lib/component.js";

const { div } = van.tags;

export const Switch = component({
  name: "switch",
  css: `
    .switch{width:34px; height:19px; border-radius:20px; background:var(--green); position:relative; cursor:pointer; flex:none;}
    .switch::after{content:""; position:absolute; width:15px; height:15px; border-radius:50%; background:var(--on-accent); top:2px; left:17px; transition:.18s; box-shadow:0 1px 3px rgba(0,0,0,.2);}
    .switch.off{background:var(--line);} .switch.off::after{left:2px;}
  `,
  // on: () => boolean (reactive); onToggle: () => void.
  view: ({ on, onToggle } = {}) =>
    div({
      class: () => "switch" + (on() ? "" : " off"),
      onclick: onToggle,
    }),
});
