// services/artifacts.js - artifact endpoints outside the turn stream: the
// short-lived signed ticket for the separate-origin preview (security F3 -
// html-artifact.js frames the returned URL). http.* -> return; no store to
// mutate, the caller owns the iframe.
import * as http from "./http.js";
import * as chat from "../state/chat.js";
import type { WireArtifactTicket } from "./wire.js";

const pid = () => chat.activeProjectId.val;

// Rejects when ticketing is unavailable - callers fall back to the in-place srcdoc path.
export const ticket = (artifactId: string) =>
  http.get<WireArtifactTicket>(`/projects/${pid()}/artifacts/${artifactId}/ticket`);
