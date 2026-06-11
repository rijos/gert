// components/main/model-picker.js — dropdown of models + capability badges +
// a per-model settings cogwheel (temperature/top-p/max-tokens via the modal).
// Binds to state/models (van-x list + selected). Uses ui/menu + badge.
import van from "van";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { Menu } from "../ui/menu.js";
import { Badge } from "../ui/badge.js";
import { openModelSettings } from "../settings/model-settings-modal.js";
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
              { style: "color:var(--ink-3);font-weight:400;font-size:var(--fs-xs)" },
              " · fast",
            )
          : null,
      ),
      button(
        {
          class: "m-gear",
          title: "Model settings (temperature, top-p, max tokens)",
          onclick: (e) => {
            e.stopPropagation(); // don't select the model
            openModelSettings(m);
          },
        },
        Icon("gear", { size: 13, strokeWidth: 2 }),
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

export const ModelPicker = component({
  name: "model-picker",
  css: `
    .model-picker{position:relative;}
    .model-btn{display:flex; align-items:center; gap:9px; padding:7px 11px; border:1px solid var(--line); background:var(--surface); border-radius:var(--r-sm); box-shadow:var(--lift); cursor:pointer; font-family:var(--sans); font-weight:600; font-size:var(--fs-md); color:var(--ink); transition:var(--t-fast);}
    .model-btn:hover{border-color:var(--coral); background:var(--coral-soft);}
    /* status dot: green with a soft glow ring */
    .model-btn .mdot{width:7px; height:7px; border-radius:50%; background:var(--green); box-shadow:0 0 0 3px var(--green-soft);}
    .model-btn .chev{width:13px; height:13px; color:var(--ink-3); transition:var(--t-slow) var(--ease);}
    .model-picker.open .chev{transform:rotate(180deg);}
    .model-picker.open .menu{opacity:1; transform:none; pointer-events:auto;}
    .m-item{padding:var(--sp-2) var(--sp-3); border-radius:var(--r-sm); cursor:pointer; transition:var(--t-fast);}
    .m-item:hover{background:var(--surface-2);}
    .m-item.sel{background:var(--coral-soft);}
    .m-top{display:flex; align-items:center; gap:8px;}
    .m-gear{margin-left:auto; display:grid; place-items:center; width:24px; height:24px; border:none; border-radius:7px; background:none; color:var(--ink-3); cursor:pointer; opacity:0; transition:var(--t-fast); flex:none;}
    .m-item:hover .m-gear,.m-gear:focus-visible{opacity:1;}
    .m-gear:hover{background:var(--surface-2); color:var(--coral-deep);}
    .m-name{font-weight:600; font-size:var(--fs-md);}
    .m-name .star{color:var(--coral-deep); font-size:var(--fs-xs);}
    .m-id{font-family:var(--mono); font-size:var(--fs-xs); color:var(--ink-3); margin:3px 0 6px;}
    .badges{display:flex; gap:5px; flex-wrap:wrap;}
  `,
  view: () => {
    // ── logic ───────────────────────────────────
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

    // ── content ─────────────────────────────────
    return Menu({
      wrapClass: "model-picker",
      open,
      trigger,
      children: [
        div({ class: "menu-h" }, "Models · vLLM"),
        () => div(...models.models.map((m) => ModelItem(m))),
      ],
    });
  },
});
