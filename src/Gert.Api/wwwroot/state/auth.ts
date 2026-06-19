// state/auth.js - identity for the UI (user display info, admin flag).
// The access token itself lives ONLY in services/auth.js (in-memory module
// variable, never localStorage - security F2). This store holds the
// non-secret, displayable identity claims.
import van from "/lib/van.js";

// Non-secret, displayable identity claims for the UI.
export interface Identity {
  username: string;
  email: string;
  avatar: string;
  authLine: string;
  isAdmin?: boolean;
}

export const user = van.state<Identity | null>(null);
export const isAdmin = van.state(false);
export const ready = van.state(false); // session resolution finished

export const setIdentity = (u: Identity | null) => {
  user.val = u;
  isAdmin.val = !!(u && u.isAdmin);
};
