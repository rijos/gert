// components/main/caret.js — streaming typewriter caret (.caret).
import van from "van";

const { span } = van.tags;

export const Caret = () => span({ class: "caret" });
