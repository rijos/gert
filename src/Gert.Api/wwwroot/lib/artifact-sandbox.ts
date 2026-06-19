// lib/artifact-sandbox.js - build the `srcdoc` for an artifact iframe with a
// restrictive PER-DOCUMENT CSP (security F3).
//
// The iframe is already opaque-origin (sandbox without allow-same-origin), so a
// rendered artifact can't reach the app's token/DOM/cookies. This meta-CSP is
// the second half of F3 - the *egress* brake: with connect-src/img/form locked
// to nothing-external, a prompt-injected page can't beacon data out or POST a
// phishing form to an attacker. NOTE: framed under the app shell, a srcdoc
// document also INHERITS the app's CSP (script-src 'self'), which blocks the
// artifact's inline scripts outright - the fallback is a static render. Full
// script fidelity belongs to the PREFERRED served-origin path (its response
// headers are its own); this meta-CSP grants scripts for the standalone case
// and stays the egress brake either way.
//
// CSP delivered via <meta http-equiv> only governs what the parser encounters
// AFTER it, so the tag must be the FIRST child of <head>. Browsers intersect
// multiple policies (most-restrictive wins), so a document shipping its own CSP
// can only tighten ours - never loosen it.

const buildCsp = (allowScripts: boolean) =>
  [
    "default-src 'none'", // deny by default; connect/frame/etc. inherit this
    allowScripts ? "script-src 'unsafe-inline'" : "script-src 'none'",
    "style-src 'unsafe-inline'", // inline <style>/style= for fidelity, no external
    "img-src data: blob:", // inline images only - no external beacons
    "font-src data:",
    "form-action 'none'", // no fallback to default-src, so must be explicit (anti-phishing)
    "base-uri 'none'", // no fallback to default-src, so must be explicit
  ].join("; ");

// Wrap untrusted artifact markup so it carries its own restrictive CSP; the
// returned string is safe to assign to iframe.srcdoc.
export const artifactSrcdoc = (content: string, { allowScripts = false }: { allowScripts?: boolean } = {}) => {
  const meta = `<meta http-equiv="Content-Security-Policy" content="${buildCsp(allowScripts)}">`;
  const src = content || "";
  // Inject as the first thing inside <head> (the common case: a full document).
  if (/<head[^>]*>/i.test(src)) return src.replace(/<head[^>]*>/i, (m: string) => m + meta);
  // A document with <html> but no <head>: give it one, meta first.
  if (/<html[^>]*>/i.test(src))
    return src.replace(/<html[^>]*>/i, (m: string) => `${m}<head>${meta}</head>`);
  // A bare fragment (e.g. "<h1>hi</h1>" or a lone <svg>): wrap a minimal shell so
  // the meta is unambiguously first in document order.
  return `<!doctype html><html><head>${meta}</head><body style="margin:0">${src}</body></html>`;
};
