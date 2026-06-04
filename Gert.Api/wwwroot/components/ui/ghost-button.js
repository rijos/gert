// components/ui/ghost-button.js — icon-only ghost button (.ghost).
import van from "van";

const { button } = van.tags;

export const GhostButton = ({ icon, onclick, title, extraClass = "" } = {}) =>
  button(
    { class: "ghost " + extraClass, onclick, title },
    icon,
  );
