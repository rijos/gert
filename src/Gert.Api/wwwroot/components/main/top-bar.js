// components/main/top-bar.js — collapse btn · title · theme · model picker ·
// panel toggle. Tool toggles live in the composer's tools dropdown.
import van from "van";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { ConvTitle } from "./conv-title.js";
import { ThemeToggle } from "./theme-toggle.js";
import { ModelPicker } from "./model-picker.js";
import * as ui from "../../state/ui.js";

const { div, button } = van.tags;

export const TopBar = component({
  name: "top-bar",
  css: `
    /* z-index:20 lifts the topbar's stacking context (backdrop-filter creates
       one, trapping the model/tools dropdowns' own z-index) above the sibling
       canvas .panel — without it an open dropdown overhanging the panel paints
       UNDER the panel's content. Below the scrim (50) / drawers (60). */
    .topbar{height:var(--head-h); flex:none; display:flex; align-items:center; gap:12px; padding:0 22px; border-bottom:1px solid var(--line); background:color-mix(in srgb, var(--paper) 80%, transparent); backdrop-filter:blur(6px); position:relative; z-index:20;}
    .collapse-btn{margin-right:2px;}
  `,
  view: () =>
  div(
    { class: "topbar" },
    button(
      {
        class: "ghost collapse-btn",
        title: "Toggle sidebar",
        onclick: ui.toggleNav,
      },
      Icon("sidebar", { strokeWidth: 2 }),
    ),
    ConvTitle(),
    ThemeToggle(),
    ModelPicker(),
    button(
      {
        class: "ghost",
        title: "Toggle knowledge panel",
        onclick: ui.togglePanel,
      },
      Icon("panel", { strokeWidth: 2 }),
    ),
  ),
});
