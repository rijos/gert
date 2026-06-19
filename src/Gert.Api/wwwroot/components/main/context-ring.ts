// components/main/context-ring.js - the composer's context-usage circle: how
// much of the selected model's context window the conversation's last turn
// occupied (state/chat.js contextTokens / the model's `context`). An SVG
// stroke-dasharray ring; hidden until both numbers are known. Accent ring,
// shifting amber past 75% and red past 90%. CLICK opens a statistics popover
// (window, used/free, last-reply tokens + speed) - same upward Menu shell as
// the tools dropdown. The Menu is built ONCE (its outside-click closer is a
// document listener); every row is a reactive binding.
import van from "/lib/van.js";
import type { TagFunc } from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Menu } from "../ui/menu.js";
import { ProgressBar } from "../ui/progress-bar.js";
import { fmtK } from "../../lib/format.js";
import * as chat from "../../state/chat.js";
import type { Message } from "../../state/chat.js";
import * as models from "../../state/models.js";

const { div, button, span } = van.tags;
// The namespaced-tags overload types the result as Readonly<Record<string, TagFunc<Element>>>;
// the cast names exactly the two tags we destructure so noUncheckedIndexedAccess does not widen
// them to `| undefined` (runtime is byte-identical after type elision; mirrors icons/icons.ts).
const { svg, circle } = van.tags("http://www.w3.org/2000/svg") as Readonly<{
  svg: TagFunc<Element>;
  circle: TagFunc<Element>;
}>;

const R = 8; // ring radius inside a 22px viewbox
const CIRCUMFERENCE = 2 * Math.PI * R;

const colorFor = (pct: number) =>
  pct > 0.9 ? "var(--coral-deep)" : pct > 0.75 ? "var(--amber)" : "var(--coral)";

const StatRow = (label: string, value: string) =>
  div({ class: "cx-row" }, span({ class: "cx-l" }, label), span({ class: "cx-v" }, value));

export const ContextRing = component({
  name: "context-ring",
  css: `
    .ctx-ring {
      position: relative;
    }
    /* the composer sits at the viewport bottom - open the stats upward */
    .ctx-ring .menu {
      top: auto;
      bottom: calc(100% + 10px);
      left: auto;
      right: -44px;
      width: 232px;
      transform-origin: bottom right;
      transform: translateY(6px) scale(.98);
    }
    .ctx-ring.open .menu {
      opacity: 1;
      transform: none;
      pointer-events: auto;
    }
    .ctx-btn {
      display: grid;
      place-items: center;
      width: 30px;
      height: 30px;
      flex: none;
      border: none;
      background: none;
      padding: 0;
      cursor: pointer;
      border-radius: 9px;
      transition: var(--t-fast);
    }
    .ctx-btn:hover {
      background: var(--surface-2);
    }
    .ctx-btn svg {
      display: block;
    }
    .ctx-btn .track {
      stroke: var(--line);
      fill: none;
    }
    .ctx-btn .fill {
      fill: none;
      stroke-linecap: round;
      transform: rotate(-90deg);
      transform-origin: center;
      transition: stroke-dashoffset var(--t-slow) var(--ease), stroke var(--t-slow) var(--ease);
    }
    .cx-row {
      display: flex;
      align-items: baseline;
      justify-content: space-between;
      gap: 12px;
      padding: 5px 10px;
      font-size: var(--fs-sm);
    }
    .cx-row .cx-l {
      color: var(--ink-2);
    }
    .cx-row .cx-v {
      font-family: var(--mono);
      font-size: var(--fs-sm);
      color: var(--ink);
      text-align: right;
    }
    .pbar.cx-bar {
      height: 5px;
      border-radius: 3px;
      margin: 7px 10px 9px;
    }
    .pbar.cx-bar > i {
      border-radius: 3px;
    }
  `,
  // logic: the open/close state for the stats popover, its toggle handler, and
  // the pure `usage()` reading (current used/max context off the live state -
  // a plain function, not a van.derive, so it is safe in setup and the bindings
  // that call it stay in view).
  setup: () => {
    const open = van.state(false);
    const toggle = (e: Event) => {
      e.stopPropagation();
      open.val = !open.val;
    };
    const usage = () => {
      const used = chat.contextTokens.val;
      const max = models.selected.val?.context;
      return used != null && max ? { used, max, pct: Math.min(used / max, 1) } : null;
    };
    return { open, toggle, usage };
  },
  // content: the ring button trigger + the upward stats Menu.
  view: ({ open, toggle, usage }) => {
    const trigger = button(
      {
        class: "ctx-btn",
        type: "button",
        title: "Context usage - click for statistics",
        "aria-label": "Context statistics",
        // no data -> no ring (the whole control collapses out of the row)
        style: () => (usage() ? "" : "display:none"),
        onclick: toggle,
      },
      () => {
        const u = usage();
        if (!u) return span();
        return svg({ viewBox: "0 0 22 22", width: 22, height: 22 },
          circle({ class: "track", cx: 11, cy: 11, r: R, "stroke-width": 2.5 }),
          circle({ class: "fill", cx: 11, cy: 11, r: R, stroke: colorFor(u.pct), "stroke-width": 2.5, "stroke-dasharray": CIRCUMFERENCE.toFixed(2), "stroke-dashoffset": (CIRCUMFERENCE * (1 - u.pct)).toFixed(2) }),
        );
      },
    );

    return Menu({
      wrapClass: "ctx-ring",
      open,
      trigger,
      children: [
        () => {
          const u = usage();
          if (!u) return div();

          // Last completed reply's stats ride the popover too.
          // findLast is ES2023; the tsconfig lib is ES2022 (browsers ship it), so
          // this boundary cast supplies its signature without widening to any
          // (mirrors state/chat.ts setConversation). The predicate guarantees a
          // numeric tokenCount on the result.
          const last = (
            chat.messages as Message[] & {
              findLast(
                p: (m: Message) => boolean,
              ): (Message & { tokenCount: number }) | undefined;
            }
          ).findLast((m) => m.role === "assistant" && m.tokenCount != null);
          const tps =
            last && last.durationMs != null && last.durationMs > 0
              ? Math.round(last.tokenCount / (last.durationMs / 1000))
              : null;

          // `u` is non-null only when the selected model carries a `context`, so
          // a model is selected here; guard for the type checker regardless.
          const model = models.selected.val;
          if (!model) return div();
          return div(
            div({ class: "menu-h" }, "Context - " + (model.name || model.id)),
            StatRow("Window", fmtK(u.max) + " tok"),
            StatRow("Used", `${fmtK(u.used)} (${Math.round(u.pct * 100)}%)`),
            StatRow("Free", fmtK(Math.max(u.max - u.used, 0)) + " tok"),
            ProgressBar({
              value: u.used,
              max: u.max,
              color: colorFor(u.pct),
              class: "cx-bar",
            }),
            last
              ? StatRow(
                  "Last reply",
                  `${last.tokenCount} tok` + (tps != null ? ` - ${tps} tok/s` : ""),
                )
              : span(),
            last && last.durationMs != null && last.durationMs > 0
              ? StatRow("Generation", (last.durationMs / 1000).toFixed(1) + "s")
              : span(),
          );
        },
      ],
    });
  },
});
