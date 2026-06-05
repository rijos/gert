// state/auth.js — identity for the UI (user display info, admin flag).
// The access token itself lives ONLY in services/auth.js (in-memory module
// variable, never localStorage — security F2). This store holds the
// non-secret, displayable identity claims.
import van from "van";

export const user = van.state(null); // { username, email, avatar, authLine }
export const isAdmin = van.state(false);
export const ready = van.state(false); // session resolution finished

export const setIdentity = (u) => {
  user.val = u;
  isAdmin.val = !!(u && u.isAdmin);
};
