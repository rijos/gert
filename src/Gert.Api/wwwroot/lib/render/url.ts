// render/url.js - pure URL/slug safety helpers for the markdown renderer.
// Extracted from lib/markdown.js so the structural renderer (sink-side) and
// lib/markdown-links.js (the external-link UI gate) share ONE source of truth
// for sanitizeUrl/sanitizeImgUrl/isExternal/slugify. No DOM, no side effects.

// http(s)/mailto/relative-path/anchor pass; anything carrying a foreign
// scheme-colon (or a smuggled control-char / entity-encoded scheme) -> "#".
const SAFE_SCHEME = /^(https?:|mailto:|\/|#)/i;
const sanitizeUrl = (raw: string) => {
  const url = (raw || "").trim();
  // For scheme detection only, strip control chars/whitespace that smuggle
  // "java\nscript:" / "java\tscript:" AND fold an entity-encoded scheme colon
  // (javascript&colon;...) back to ":" so it can't slip past the no-foreign-
  // scheme guard. We never store this collapsed copy - the href that lands on
  // the element is the verbatim `url`, set via setAttribute (HTML-inert).
  const collapsed = url
    .replace(/[\x00-\x1f\x7f\s]/g, "")
    .replace(/&#0*58;|&#x0*3a;|&colon;/gi, ":");
  if (/^(javascript|data|vbscript):/i.test(collapsed)) return "#";
  // String.split always returns at least one element, so [0] is a defined string.
  if (SAFE_SCHEME.test(url) || /^[^:]*$/.test(collapsed.split("/")[0]!)) return url;
  return "#";
};

// Images are the one place model output can trigger an automatic network fetch,
// so the renderer allows ONLY inline data: images of known-safe media types
// (never data:text/html or data:image/svg+xml, which can script). EVERY url-
// shaped src - cross-origin, same-origin, AND relative - collapses to "#", which
// yields a broken (inert) <img>: no fetch, no script sink. (Operator policy F4;
// the model can't author a working app asset URL, so a url-shaped src is only a
// doomed request / probe surface. Inline data:image - e.g. a model-generated
// chart - is the only real image case, and it is preserved.)
const SAFE_IMG_DATA = /^data:image\/(?:png|jpe?g|gif|webp|avif|bmp|x-icon);base64,/i;
const sanitizeImgUrl = (raw: string) => {
  const url = (raw || "").trim();
  return SAFE_IMG_DATA.test(url) ? url : "#";
};

// http(s) AND protocol-relative "//host" - both leave the app origin, so both
// get target=_blank rel="noopener noreferrer" (a single "/" path stays internal).
const isExternal = (url: string) => /^https?:/i.test(url) || /^\/\//.test(url);

// GitHub-style slug `id` on every heading so in-document links ([x](#section))
// resolve. Slug derived from the heading's TEXT (the inline pass already reduced
// markup to text), folded to a `[a-z0-9_-]` token, so the id is inert even via
// setAttribute. Duplicate slugs get -1/-2 (GFM), unique within this fragment.
const slugify = (text: string) =>
  text.trim().toLowerCase()
    .replace(/[^\w\- ]+/g, "")
    .replace(/\s+/g, "-")
    .replace(/-+/g, "-")
    .replace(/^-|-$/g, "");

export { sanitizeUrl, sanitizeImgUrl, isExternal, slugify };
