// lib/action.js — the default chain for a user-initiated action: run it, and if
// it throws (network/API error), surface a toast instead of silently swallowing
// it. Returns the result, or undefined on failure (so callers can branch).
//
//   attempt(() => svc.remove(id), "Couldn't delete this chat");
//
// For background loads (boot fetches, silent refresh) keep the bare `.catch`;
// `attempt` is for things the user just asked for and should hear about.
import { toast } from "../components/ui/toast.js";

export const attempt = async (fn, errorMessage = "Something went wrong") => {
  try {
    return await fn();
  } catch {
    toast(errorMessage, "err");
    return undefined;
  }
};
