// components/main/theme-toggle.js — sun/moon toggle (CSS picks the visible one).
import van from "van";
import { ThemeGlyphs } from "../../icons/icons.js";
import * as ui from "../../state/ui.js";

const { button } = van.tags;

export const ThemeToggle = () =>
  button(
    { class: "ghost theme-toggle", title: "Toggle theme", onclick: ui.toggleTheme },
    ...ThemeGlyphs(),
  );
