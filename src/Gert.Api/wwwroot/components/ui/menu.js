// components/ui/menu.js — dropdown menu shell (.menu) with open/close state.
// Returns a wrapper whose `class` reflects `open`; clicking outside closes it.
// Owns the base .menu / .menu-h styling; the open-state reveal lives with the
// wrapping picker (.model-picker.open .menu, .project-picker.open .menu).
import van from "van";
import { component } from "../../lib/component.js";

const { div } = van.tags;

export const Menu = component({
  name: "menu",
  css: `
    .menu{position:absolute; top:calc(100% + 8px); right:0; width:312px; background:var(--surface); border:1px solid var(--line); border-radius:var(--r); box-shadow:0 18px 44px -14px rgba(60,46,28,.34), 0 2px 8px rgba(60,46,28,.08); padding:7px; opacity:0; transform:translateY(-6px) scale(.98); transform-origin:top right; pointer-events:none; transition:.2s cubic-bezier(.2,.8,.2,1); z-index:30;}
    .menu-h{font-family:var(--mono); font-size:10px; letter-spacing:.08em; text-transform:uppercase; color:var(--ink-3); padding:8px 10px 6px;}
  `,
  // trigger: node (the button). open: van.state(boolean). header optional.
  // children: menu rows.
  view: ({ trigger, open, wrapClass = "model-picker", children = [] } = {}) => {
    // ── logic ───────────────────────────────────
    const onDoc = () => (open.val = false);
    document.addEventListener("click", onDoc);

    // ── content ─────────────────────────────────
    return div(
      { class: () => wrapClass + (open.val ? " open" : "") },
      trigger,
      div(
        {
          class: "menu",
          onclick: (e) => e.stopPropagation(),
        },
        ...children,
      ),
    );
  },
});
