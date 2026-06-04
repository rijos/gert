// components/ui/pill.js — status pill (ready / proc / fail).
import van from "van";

const { span } = van.tags;

const LABELS = { ready: "Ready", proc: "Processing", fail: "Failed" };

// kind: "ready" | "proc" | "fail"; label optional override.
export const Pill = ({ kind = "ready", label } = {}) =>
  span(
    { class: "pill " + kind },
    span({ class: "pd" }),
    label || LABELS[kind] || kind,
  );
