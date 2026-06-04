// components/ui/badge.js — capability / meta badge.
import van from "van";

const { span } = van.tags;

// cap: true → accent capability badge; otherwise neutral meta badge.
export const Badge = ({ label, cap = false } = {}) =>
  span({ class: "badge" + (cap ? " cap" : "") }, label);
