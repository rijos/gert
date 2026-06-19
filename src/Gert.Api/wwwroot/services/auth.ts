// services/auth.js - PKCE login, in-memory token store, silent refresh.
//
// SECURITY F2: the access token is held in a MODULE-LOCAL variable below.
// It is NEVER written to localStorage / sessionStorage / cookies from JS, so
// an injected script has nothing persistent to exfiltrate. A long-lived
// refresh token, if any, lives in an httpOnly Secure SameSite=Strict cookie
// set by the server - never readable here.
import * as authState from "../state/auth.js";

// The two dev/boot globals this module reads off `window` (both inert in prod):
// the OIDC overrides and the E2E-injected token (see ensureSession).
declare global {
  interface Window {
    GERT_AUTH?: { authority?: string; clientId?: string };
    GERT_DEV_TOKEN?: string;
  }
}

// The OIDC token endpoint response (access_token + lifetime).
interface TokenResponse {
  access_token: string;
  expires_in: number;
}

// The non-secret JWT claims this module reads for the display identity.
interface Claims {
  preferred_username?: string;
  name?: string;
  email?: string;
  groups?: unknown;
  roles?: unknown;
}

// ---- in-memory token (F2) --------------------------------------------------
let accessToken: string | null = null;
let tokenExpiry = 0; // epoch ms
let refreshTimer: ReturnType<typeof setTimeout> | null = null;

export const getToken = () => accessToken;

const setToken = (token: string, expiresInSec: number) => {
  accessToken = token;
  tokenExpiry = Date.now() + (expiresInSec || 0) * 1000;
  scheduleRefresh();
};

export const clearToken = () => {
  accessToken = null;
  tokenExpiry = 0;
  if (refreshTimer) clearTimeout(refreshTimer);
};

// ---- OIDC config -----------------------------------------------------------
const CONFIG = {
  authority: window.GERT_AUTH?.authority || "/oidc",
  clientId: window.GERT_AUTH?.clientId || "gert-spa",
  redirectUri: location.origin + "/",
  scope: "openid profile email",
};
const VERIFIER_KEY = "gert.pkce.verifier";

// ---- PKCE helpers (Web Crypto, no deps) ------------------------------------
const b64url = (bytes: ArrayBuffer | Uint8Array) =>
  btoa(String.fromCharCode(...new Uint8Array(bytes)))
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/, "");

const randomVerifier = () => b64url(crypto.getRandomValues(new Uint8Array(32)));

const challenge = async (verifier: string) =>
  b64url(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier)));

// ---- claims (non-secret display identity) ----------------------------------
const decodeClaims = (jwt: string): Claims => {
  try {
    // The middle JWT segment; noUncheckedIndexedAccess makes it possibly
    // undefined, but the throw on a malformed token is caught below (returns {}).
    const payload = jwt.split(".")[1]!.replace(/-/g, "+").replace(/_/g, "/");
    // The decoded JWT payload is the non-secret claims object (wire boundary).
    return JSON.parse(atob(payload)) as Claims;
  } catch {
    return {};
  }
};

const applyIdentity = (claims: Claims) => {
  const username = claims.preferred_username || claims.name || "user";
  const groups = claims.groups || claims.roles || [];
  authState.setIdentity({
    username,
    email: claims.email || "",
    avatar: (username[0] || "G").toUpperCase(),
    authLine: "via Pocket ID",
    isAdmin: Array.isArray(groups) && groups.includes("gert-admins"),
  });
};

// ---- flow ------------------------------------------------------------------
const beginLogin = async () => {
  const verifier = randomVerifier();
  sessionStorage.setItem(VERIFIER_KEY, verifier); // verifier, NOT a token - short-lived, by design
  const code = await challenge(verifier);
  const url = new URL(CONFIG.authority + "/authorize");
  url.search = new URLSearchParams({
    response_type: "code",
    client_id: CONFIG.clientId,
    redirect_uri: CONFIG.redirectUri,
    scope: CONFIG.scope,
    code_challenge: code,
    code_challenge_method: "S256",
    state: b64url(crypto.getRandomValues(new Uint8Array(16))),
  }).toString();
  location.assign(url.toString());
};

const exchangeCode = async (code: string): Promise<TokenResponse> => {
  const verifier = sessionStorage.getItem(VERIFIER_KEY);
  sessionStorage.removeItem(VERIFIER_KEY);
  const res = await fetch(CONFIG.authority + "/token", {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    credentials: "include", // lets the server set the httpOnly refresh cookie
    body: new URLSearchParams({
      grant_type: "authorization_code",
      code,
      client_id: CONFIG.clientId,
      redirect_uri: CONFIG.redirectUri,
      code_verifier: verifier || "",
    }),
  });
  if (!res.ok) throw new Error("token exchange failed");
  return res.json();
};

// Silent refresh via the httpOnly refresh cookie (no token in JS storage).
const refresh = async () => {
  const res = await fetch(CONFIG.authority + "/token", {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    credentials: "include",
    body: new URLSearchParams({
      grant_type: "refresh_token",
      client_id: CONFIG.clientId,
    }),
  });
  if (!res.ok) return false;
  const tok = (await res.json()) as TokenResponse;
  setToken(tok.access_token, tok.expires_in);
  applyIdentity(decodeClaims(tok.access_token));
  return true;
};

const scheduleRefresh = () => {
  if (refreshTimer) clearTimeout(refreshTimer);
  const lead = 60_000; // refresh ~1 min before expiry
  const delay = Math.max(tokenExpiry - Date.now() - lead, 5_000);
  refreshTimer = setTimeout(() => refresh().catch(() => {}), delay);
};

// ensureSession - called once at boot. Handles the redirect callback, then a
// silent refresh; falls back to starting the PKCE login.
export const ensureSession = async () => {
  // DEV/TEST ONLY (testing.md section 4.3/section 9): the Python E2E harness injects a minted
  // token as window.GERT_DEV_TOKEN via a Playwright init script (the token is
  // otherwise in-memory only - F2). When present, install it and skip PKCE. The
  // global is never set in production, so this branch is inert there.
  if (window.GERT_DEV_TOKEN) {
    setToken(window.GERT_DEV_TOKEN, 3600);
    applyIdentity(decodeClaims(window.GERT_DEV_TOKEN));
    authState.ready.val = true;
    return;
  }

  const params = new URLSearchParams(location.search);
  const code = params.get("code");
  try {
    if (code) {
      const tok = await exchangeCode(code);
      setToken(tok.access_token, tok.expires_in);
      applyIdentity(decodeClaims(tok.access_token));
      history.replaceState({}, "", location.pathname); // drop ?code=
    } else if (!(await refresh())) {
      await beginLogin();
      return; // navigating away
    }
  } catch {
    await beginLogin();
    return;
  }
  authState.ready.val = true;
};

export const logout = () => {
  clearToken();
  beginLogin();
};
