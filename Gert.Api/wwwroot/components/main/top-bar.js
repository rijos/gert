// components/main/top-bar.js — collapse btn · title · tool chips · theme ·
// model picker · panel toggle.
import van from "van";
import { Icon } from "../../icons/icons.js";
import { ConvTitle } from "./conv-title.js";
import { ToolChips } from "./tool-chips.js";
import { ThemeToggle } from "./theme-toggle.js";
import { ModelPicker } from "./model-picker.js";
import * as ui from "../../state/ui.js";

const { div, button } = van.tags;

export const TopBar = () =>
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
    ToolChips(),
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
  );
