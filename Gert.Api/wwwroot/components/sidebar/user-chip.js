// components/sidebar/user-chip.js — avatar + name + auth line + settings button.
import van from "van";
import { Icon } from "../../icons/icons.js";
import * as auth from "../../state/auth.js";
import { navigate } from "../../lib/router.js";

const { div, button } = van.tags;

export const UserChip = () =>
  div(
    { class: "userchip" },
    div({ class: "avatar" }, () => auth.user.val?.avatar || "G"),
    div(
      { class: "who" },
      div({ class: "name" }, () => auth.user.val?.username || "—"),
      div(
        { class: "auth" },
        Icon("lock", { size: 9, strokeWidth: 2.4 }),
        () =>
          (auth.user.val?.authLine || "via Pocket ID") +
          (auth.isAdmin.val ? " · admin" : ""),
      ),
    ),
    button(
      { class: "ghost", title: "Settings", onclick: () => navigate("/settings") },
      Icon("gear", { strokeWidth: 2 }),
    ),
  );
