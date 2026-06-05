// components/main/model-picker.js — dropdown of models + capability badges.
// Binds to state/models (van-x list + selected). Uses ui/menu + badge.
import van from "van";
import { Icon } from "../../icons/icons.js";
import { Menu } from "../ui/menu.js";
import { Badge } from "../ui/badge.js";
import * as models from "../../state/models.js";

const { div, button, span } = van.tags;

const ModelItem = (m) =>
  div(
    {
      class: () => "m-item" + (models.selectedId.val === m.id ? " sel" : ""),
      onclick: () => models.select(m.id),
    },
    div(
      { class: "m-top" },
      span(
        { class: "m-name" },
        m.name,
        m.default ? span({ class: "star" }, " ★ default") : null,
        m.fast
          ? span(
              { style: "color:var(--ink-faint);font-weight:400;font-size:11px" },
              " · fast",
            )
          : null,
      ),
    ),
    div({ class: "m-id" }, (m.id || "") + (m.endpoint ? " · " + m.endpoint : "")),
    div(
      { class: "badges" },
      ...(m.capabilities || []).map((c) => Badge({ label: c, cap: true })),
      m.context != null
        ? Badge({ label: Math.round(m.context / 1024) + "K ctx" })
        : null,
    ),
  );

export const ModelPicker = () => {
  const open = van.state(false);

  const trigger = button(
    {
      class: "model-btn",
      onclick: (e) => {
        e.stopPropagation();
        open.val = !open.val;
      },
    },
    span({ class: "mdot" }),
    span({ class: "mname" }, () => models.selected.val?.name || "Model"),
    Icon("chevron", { size: 13, class: "chev", strokeWidth: 2.4 }),
  );

  return Menu({
    wrapClass: "model-picker",
    open,
    trigger,
    children: [
      div({ class: "menu-h" }, "Models · vLLM"),
      () => div(...models.models.map((m) => ModelItem(m))),
    ],
  });
};
