// state/health.js - connection health. `degraded` is owned by services/http.js:
// true once a request exhausts retries on a network/5xx failure, false on the
// next success. ConnectionBanner reads it.
import van from "/lib/van.js";

export const degraded = van.state(false);
