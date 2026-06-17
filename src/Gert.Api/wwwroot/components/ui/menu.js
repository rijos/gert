// components/ui/menu.js - dropdown menu shell (.menu) with open/close state.
// Returns a wrapper whose `class` reflects `open`; clicking outside closes it.
// Owns the base .menu / .menu-h styling; the open-state reveal lives with the
// wrapping picker (.model-picker.open .menu, .project-picker.open .menu).
import van from "/lib/van.js";
import { component } from "../../lib/component.js";

const { div } = van.tags;

export const Menu = component({
  name: "menu",
  css: `
    .menu{position:absolute; top:calc(100% + 8px); right:0; width:312px; background:var(--surface); border:1px solid var(--line); border-radius:var(--r); box-shadow:var(--shadow-menu); padding:6px; opacity:0; transform:translateY(-6px) scale(.98); transform-origin:top right; pointer-events:none; transition:var(--t-slow) var(--ease); z-index:30;}
    .menu-h{font-family:var(--mono); font-size:var(--fs-2xs); letter-spacing:.08em; text-transform:uppercase; color:var(--ink-3); padding:8px 10px 6px;}
  `,
  // trigger: node (the button). open: van.state(boolean). header optional.
  // children: menu rows.
  view: ({ trigger, open, wrapClass = "model-picker", children = [] } = {}) => {
    // -- logic -----------------------------------
    const onDoc = () => {
      open.val = false;
      // a menu unmounted while open never re-runs the pruned derive below -
      // self-detach so the closer can't outlive its menu (section 12 cleanup).
      if (!wrap.isConnected) document.removeEventListener("click", onDoc);
    };

    // -- content ---------------------------------
    const wrap = div(
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
    // Listen only while open (section 12): the document closer exists only for an
    // open menu, and scoping the derive to `wrap` (van.derive's third arg)
    // prunes it once the menu leaves the DOM - nothing leaks per render.
    // Safe ordering: every trigger stopPropagation()s its toggle click, and
    // the derive flushes in a microtask, so the opening click can't reach a
    // just-added document listener and self-close.
    van.derive(
      () => {
        if (open.val) document.addEventListener("click", onDoc);
        else document.removeEventListener("click", onDoc);
      },
      undefined,
      wrap,
    );
    return wrap;
  },
});
