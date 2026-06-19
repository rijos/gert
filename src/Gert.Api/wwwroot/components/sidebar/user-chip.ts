// components/sidebar/user-chip.js - avatar + name + auth line + settings
// button; admins additionally get a shield button into the admin panel.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { navigate } from "../../lib/router.js";
import * as auth from "../../state/auth.js";
import { openSettings } from "../settings/settings-modal.js";
import { t } from "../../lib/i18n.js";

const { div, button } = van.tags;

export const UserChip = component({
  name: "user-chip",
  css: `
    .userchip {
      border-top: 1px solid var(--line);
      padding: 13px 16px;
      display: flex;
      align-items: center;
      gap: 11px;
    }

    .avatar {
      width: 32px;
      height: 32px;
      border-radius: 9px;
      flex: none;
      background: linear-gradient(140deg,var(--coral),var(--coral-deep));
      color: var(--on-accent);
      font-family: var(--display);
      font-weight: 600;
      font-size: var(--fs-base);
      display: grid;
      place-items: center;
    }

    .userchip .who {
      flex: 1;
      min-width: 0;
    }

    .userchip .name {
      font-weight: 600;
      font-size: var(--fs-md);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }

    .userchip .auth {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      color: var(--ink-3);
      display: flex;
      align-items: center;
      gap: 4px;
    }

    .userchip .auth svg {
      width: 9px;
      height: 9px;
    }
  `,
  view: () =>
    div({ class: "userchip" },
      div({ class: "avatar" }, () => auth.user.val?.avatar || "G"),
      div({ class: "who" },
        div({ class: "name" }, () => auth.user.val?.username || "-"),
        div({ class: "auth" },
          Icon("lock", { size: 9, strokeWidth: 2.4 }),
          () =>
            (auth.user.val?.authLine || "via Pocket ID") +
            (auth.isAdmin.val ? " - admin" : ""),
        ),
      ),
      // server enforces the Admin policy; this is discovery, not a gate.
      () =>
        auth.isAdmin.val
          ? button({ class: "ghost", title: t("Admin panel"), "aria-label": "Open the admin panel", onclick: () => navigate("/admin/users") },
              Icon("shield", { strokeWidth: 2 }),
            )
          : div(),
      button({ class: "ghost", title: t("Settings"), "aria-label": t("Settings"), onclick: openSettings },
        Icon("gear", { strokeWidth: 2 }),
      ),
    ),
});
