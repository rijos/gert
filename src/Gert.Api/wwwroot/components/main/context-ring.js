// components/main/context-ring.js — the composer's context-usage circle: how
// much of the selected model's context window the conversation's last turn
// occupied (state/chat.js contextTokens ÷ the model's `context`). An SVG
// stroke-dasharray ring; hidden until both numbers are known. Accent ring,
// shifting amber past 75% and red past 90%.
import van from "van";
import { component } from "../../lib/component.js";
import * as chat from "../../state/chat.js";
import * as models from "../../state/models.js";

const { div } = van.tags;
const { svg, circle } = van.tags("http://www.w3.org/2000/svg");

const R = 8; // ring radius inside a 22px viewbox
const CIRCUMFERENCE = 2 * Math.PI * R;

const fmtK = (n) =>
  n >= 1000 ? (n / 1000).toFixed(1).replace(/\.0$/, "") + "K" : String(n);

export const ContextRing = component({
  name: "context-ring",
  css: `
    .ctx-ring{display:grid; place-items:center; width:30px; height:30px; flex:none; cursor:default;}
    .ctx-ring svg{display:block;}
    .ctx-ring .track{stroke:var(--line-strong); fill:none;}
    .ctx-ring .fill{fill:none; stroke-linecap:round; transform:rotate(-90deg); transform-origin:center; transition:stroke-dashoffset .35s ease, stroke .35s ease;}
  `,
  view: () => () => {
    const used = chat.contextTokens.val;
    const max = models.selected.val?.context;
    if (used == null || !max) return div();

    const pct = Math.min(used / max, 1);
    const stroke =
      pct > 0.9 ? "var(--accent-deep)" : pct > 0.75 ? "var(--amber)" : "var(--accent)";

    return div(
      {
        class: "ctx-ring",
        title: `Context: ${fmtK(used)} / ${fmtK(max)}`,
        "aria-label": `Context usage ${Math.round(pct * 100)} percent`,
      },
      svg(
        { viewBox: "0 0 22 22", width: 22, height: 22 },
        circle({ class: "track", cx: 11, cy: 11, r: R, "stroke-width": 2.5 }),
        circle({
          class: "fill",
          cx: 11,
          cy: 11,
          r: R,
          stroke,
          "stroke-width": 2.5,
          "stroke-dasharray": CIRCUMFERENCE.toFixed(2),
          "stroke-dashoffset": (CIRCUMFERENCE * (1 - pct)).toFixed(2),
        }),
      ),
    );
  },
});
