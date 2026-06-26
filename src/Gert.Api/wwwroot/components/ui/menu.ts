// components/ui/menu.js - dropdown menu shell (.menu) over a native <div popover="auto">.
// The consumer's van.State<boolean> is the single source of truth: a van.derive drives the
// popover open/closed, and the popover's `toggle` event mirrors native-initiated closes
// (light-dismiss / Esc) back into it.
// Owns the base .menu / .menu-h styling; entry animation via @starting-style.
import van from "/lib/van.js";
import type { State, ChildDom } from "/lib/van.js";
import { component } from "../../lib/component.js";

const { div } = van.tags;

interface MenuProps {
  trigger: ChildDom;
  open: State<boolean>;
  wrapClass?: string;
  children?: ChildDom[];
  // Positioning: "bottom-right" (default, downward right-aligned),
  // "bottom-left" (downward left-aligned, full-width for dropdowns),
  // "top-right" (upward right-aligned, for bottom-anchored menus).
  align?: "bottom-right" | "bottom-left" | "top-right";
}

let menuSeq = 0;

// CSS anchor positioning lets the popover follow its trigger with no per-open measurement and
// no scroll drift. Chromium/WebKit support it; Firefox (still) doesn't, so feature-detect and
// fall back to one-shot JS placement.
const supportsAnchor = CSS.supports("anchor-name: --x");

export const Menu = component({
  name: "menu",
  css: `
    /* base selector stays at (0,1,0) so the .app-prefixed mobile width clamp in layout.css
       (section 3) still wins by specificity - a [popover] qualifier here would tie and lose. */
    .menu {
      position: fixed;
      left: auto;
      right: auto;
      top: auto;
      bottom: auto;
      width: 312px;
      background: var(--surface);
      border: 1px solid var(--line);
      border-radius: var(--r);
      box-shadow: var(--shadow-menu);
      padding: 6px;
      transition: opacity var(--t-slow) var(--ease), transform var(--t-slow) var(--ease);
    }
    /* Where CSS anchor positioning is supported the popover tracks its trigger via these insets
       (it stays put on scroll); the gap is baked into calc() so nothing leaks. Firefox lacks
       anchor() today - these rules drop wholesale there and the toggle handler places it. */
    .menu[data-align="bottom-right"] { top: calc(anchor(bottom) + 8px); right: anchor(right); }
    .menu[data-align="bottom-left"] { top: calc(anchor(bottom) + 8px); left: anchor(left); width: anchor-size(width); }
    .menu[data-align="top-right"] { bottom: calc(anchor(top) + 10px); right: anchor(right); }
    @starting-style {
      [popover]:popover-open {
        opacity: 0;
        transform: translateY(-6px) scale(.98);
      }
    }
    .menu-h {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      letter-spacing: .08em;
      text-transform: uppercase;
      color: var(--ink-3);
      padding: 8px 10px 6px;
    }
  `,
  view: ({ trigger, open, wrapClass = "model-picker", children = [], align = "bottom-right" }: MenuProps = {} as MenuProps) => {
    const seq = ++menuSeq;
    const id = `menu-${seq}`;

    const menuEl = div(
      { id, popover: "auto", class: "menu", "data-align": align, onclick: (e: Event) => e.stopPropagation() },
      ...children,
    );

    const wrap = div(
      {
        class: () => wrapClass + (open.val ? " open" : ""),
      },
      trigger,
      menuEl,
    );

    // The trigger is the first direct-child <button> (every consumer passes one, or an array
    // whose last element is a conditional <button>). It already toggles open.val via its own
    // onclick; here we mark it as a popup trigger and, where supported, wire it as the CSS anchor
    // so the popover tracks it declaratively (the data-align rules above then place it).
    const triggerBtn = wrap.querySelector<HTMLButtonElement>(":scope > button");
    if (triggerBtn) {
      triggerBtn.setAttribute("aria-haspopup", "true");
      if (supportsAnchor) {
        triggerBtn.style.setProperty("anchor-name", `--menu-${seq}`);
        menuEl.style.setProperty("position-anchor", `--menu-${seq}`);
      }
    }

    // open.val drives the popover. The :popover-open guards keep show/hidePopover idempotent
    // (calling either in the wrong state throws). Scoped to `wrap` (van.derive's 3rd arg) so
    // it's pruned when the menu leaves the DOM.
    (van.derive as (f: () => void, s: undefined, dom: Element) => unknown)(
      () => {
        if (open.val) {
          if (!menuEl.matches(":popover-open")) menuEl.showPopover();
        } else {
          if (menuEl.matches(":popover-open")) menuEl.hidePopover();
        }
      },
      undefined,
      wrap,
    );

    // Sync native popover state -> open.val. Without anchor positioning, also measure-and-place
    // the popover relative to the trigger on each open (the Firefox fallback).
    menuEl.addEventListener("toggle", (e: Event) => {
      const te = e as ToggleEvent;
      open.val = te.newState === "open";
      if (te.newState === "open" && triggerBtn && !supportsAnchor) {
        const r = triggerBtn.getBoundingClientRect();
        if (align === "bottom-left") {
          menuEl.style.top = `${r.bottom + 8}px`;
          menuEl.style.left = `${r.left}px`;
          menuEl.style.width = `${r.width}px`;
        } else if (align === "top-right") {
          menuEl.style.bottom = `${window.innerHeight - r.top + 10}px`;
          menuEl.style.right = `${window.innerWidth - r.right}px`;
        } else {
          menuEl.style.top = `${r.bottom + 8}px`;
          menuEl.style.right = `${window.innerWidth - r.right}px`;
        }
      }
    });

    return wrap;
  },
});
