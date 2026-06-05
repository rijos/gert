// components/sidebar/brand.js — mark + title + drawer-close (the pane header).
// The version line lives in the settings modal.
import van from "van";
import { BrandMark, Icon } from "../../icons/icons.js";
import * as ui from "../../state/ui.js";

const { div, h1, button } = van.tags;

export const Brand = () =>
  div(
    { class: "brand" },
    div({ class: "mark" }, BrandMark()),
    h1("Gert"),
    button(
      {
        class: "ghost drawer-close",
        style: "margin-left:auto",
        title: "Close",
        onclick: ui.toggleNav,
      },
      Icon("close", { strokeWidth: 2.2 }),
    ),
  );
