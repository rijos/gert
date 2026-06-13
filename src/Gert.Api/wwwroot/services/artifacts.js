// services/artifacts.js - artifact endpoints outside the turn stream: the
// short-lived signed ticket for the separate-origin preview (security F3 -
// html-artifact.js frames the returned URL). http.* -> return; no store to
// mutate, the caller owns the iframe.
import * as http from "./http.js";
import * as chat from "../state/chat.js";

const pid = () => chat.activeProjectId.val;

// Mint a preview ticket for an artifact; resolves { url } (or rejects when
// ticketing is unavailable - callers fall back to the in-place srcdoc path).
export const ticket = (artifactId) =>
  http.get(`/projects/${pid()}/artifacts/${artifactId}/ticket`);
