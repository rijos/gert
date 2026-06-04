// components/sidebar/brand.js — mark + title + version + drawer-close.
import van from "van";
import { BrandMark, Icon } from "../../icons/icons.js";
import * as ui from "../../state/ui.js";

const { div, h1, button } = van.tags;

export const Brand = () =>
  div(
    { class: "brand" },
    div({ class: "mark" }, BrandMark()),
    div(h1("Gert"), div({ class: "ver" }, "v0 · homelab · 20u")),
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
