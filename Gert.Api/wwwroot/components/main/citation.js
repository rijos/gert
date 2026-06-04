// components/main/citation.js — inline [n] superscript marker.
import van from "van";

const { span } = van.tags;

export const Citation = ({ ordinal, label } = {}) =>
  span({ class: "cite", title: label || "" }, String(ordinal));
