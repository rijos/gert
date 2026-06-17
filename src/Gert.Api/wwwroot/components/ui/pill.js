// components/ui/pill.js - status pill (ready / proc / fail).
import van from "/lib/van.js";
import { component } from "../../lib/component.js";

const { span } = van.tags;

const LABELS = { ready: "Ready", proc: "Processing", fail: "Failed" };

export const Pill = component({
  name: "pill",
  css: `
    .pill{font-family:var(--mono); font-size:var(--fs-2xs); padding:3px 7px; border-radius:20px; font-weight:500; display:flex; align-items:center; gap:4px; flex:none;}
    .pill .pd{width:5px; height:5px; border-radius:50%;}
    .pill.ready{background:var(--coral-soft); color:var(--coral-deep);} .pill.ready .pd{background:var(--coral);}
    .pill.proc{background:var(--proc-bg); color:var(--proc-fg);} .pill.proc .pd{background:var(--amber); animation:pulse 1.1s infinite;}
    .pill.fail{background:var(--fail-bg); color:var(--fail-fg);} .pill.fail .pd{background:var(--brick);}
  `,
  // kind: "ready" | "proc" | "fail"; label optional override.
  view: ({ kind = "ready", label } = {}) =>
    span(
      { class: "pill " + kind },
      span({ class: "pd" }),
      label || LABELS[kind] || kind,
    ),
});
