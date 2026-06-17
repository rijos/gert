// components/ui/slider.js - labeled range slider (.slider) with a live mono
// value readout, e.g. the generation-param dials in settings. The track fills
// coral up to the thumb via the --fill custom property (no JS layout).
import van from "/lib/van.js";
import { component } from "../../lib/component.js";

const { div, input, span } = van.tags;

export const Slider = component({
  name: "slider",
  css: `
    .slider{display:flex; flex-direction:column; gap:5px;}
    .slider .s-head{display:flex; align-items:baseline; gap:8px;}
    .slider .s-label{flex:1; font-size:var(--fs-sm); font-weight:500; color:var(--ink-2);}
    .slider .s-val{font-family:var(--mono); font-size:var(--fs-xs); color:var(--coral-deep);}
    .slider input[type="range"]{appearance:none; -webkit-appearance:none; width:100%; height:4px; border-radius:2px; background:linear-gradient(to right, var(--coral) var(--fill,50%), var(--line) var(--fill,50%)); outline:none; cursor:pointer;}
    .slider input[type="range"]::-webkit-slider-thumb{appearance:none; -webkit-appearance:none; width:14px; height:14px; border-radius:50%; background:var(--coral-deep); border:2px solid var(--surface); box-shadow:var(--lift);}
    .slider input[type="range"]::-moz-range-thumb{width:11px; height:11px; border-radius:50%; background:var(--coral-deep); border:2px solid var(--surface); box-shadow:var(--lift);}
    .slider input[type="range"]:focus-visible{outline:2px solid var(--coral); outline-offset:3px;}
    .slider.disabled{opacity:.45; pointer-events:none;}
  `,
  // label: string; min/max/step: numbers; value: () => number;
  // onInput: (number) => void; format: (number) => string;
  // disabled: () => boolean (greys the dial out, e.g. behind a master switch).
  view: ({
    label,
    min,
    max,
    step,
    value,
    onInput,
    format = (v) => String(v),
    disabled = () => false,
  } = {}) =>
    div(
      { class: () => "slider" + (disabled() ? " disabled" : "") },
      div(
        { class: "s-head" },
        span({ class: "s-label" }, label),
        span({ class: "s-val" }, () => format(value())),
      ),
      input({
        type: "range",
        min,
        max,
        step,
        value: () => value(),
        style: () => "--fill:" + (((value() - min) / (max - min)) * 100).toFixed(1) + "%",
        "aria-label": label,
        oninput: (e) => onInput(Number(e.target.value)),
      }),
    ),
});
