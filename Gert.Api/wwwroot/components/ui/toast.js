// components/ui/toast.js — transient toast notifications.
// toast(message, kind) mounts into a fixed host and auto-dismisses.
import van from "van";

const { div } = van.tags;

let host = null;
const ensureHost = () => {
  if (!host) {
    host = div({ class: "toast-host" });
    van.add(document.body, host);
  }
  return host;
};

// kind: "info" | "ok" | "err"
export const toast = (message, kind = "info", ms = 3200) => {
  const el = div({ class: "toast" + (kind === "info" ? "" : " " + kind) }, message);
  van.add(ensureHost(), el);
  setTimeout(() => el.remove(), ms);
};

export const Toast = toast; // PascalCase alias for the convention
