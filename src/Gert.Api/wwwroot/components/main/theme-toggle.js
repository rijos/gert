// components/main/theme-toggle.js - sun/moon toggle. Which glyph shows is driven
// by --sun-display / --moon-display tokens (flipped under dark in tokens.css), so
// this component reads tokens only - no @media, per the styleguide.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { ThemeGlyphs } from "../../icons/icons.js";
import * as ui from "../../state/ui.js";

const { button } = van.tags;

export const ThemeToggle = component({
  name: "theme-toggle",
  css: `
    .theme-toggle .sun{display:var(--sun-display);}
    .theme-toggle .moon{display:var(--moon-display);}
  `,
  view: () =>
    button(
      { class: "ghost theme-toggle", title: "Toggle theme", onclick: ui.toggleTheme },
      ...ThemeGlyphs(),
    ),
});
