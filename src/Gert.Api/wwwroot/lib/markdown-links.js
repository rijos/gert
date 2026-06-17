// lib/markdown-links.js - "open external link?" confirm for rendered markdown
// bodies, using Gert's Modal. Deliberately separate from lib/markdown.js so the
// renderer stays pure, synchronous and side-effect-free (security F4): it only
// builds inert, sanitizeUrl-scrubbed <a> nodes. This is the UI gate that shows
// the real destination before the browser leaves the app - so a model-authored
// link never navigates silently.
//
// One DELEGATED listener on the host the consumer owns (not a handler per <a>),
// so it survives VanJS re-renders with no per-link cleanup and the host's own
// disconnect collects it (spa-style-guide section 12). Mirrors lib/action.js
// reaching into components/ui for transient UI (toast).
import { Modal } from "../components/ui/modal.js";
import { t } from "./i18n.js";
import { isExternal } from "./render/url.js";

// isExternal (./render/url.js, shared with the renderer): leaves the app origin
// on http(s):// or protocol-relative //host. In-doc (#...) and same-origin
// relative links navigate without a prompt.

export const attachLinkConfirm = (host) => {
  host.addEventListener("click", (e) => {
    const a = e.target.closest && e.target.closest("a[href]");
    if (!a || !host.contains(a)) return;
    const href = a.getAttribute("href");
    if (!href || !isExternal(href)) return; // "#", in-doc anchors, relative: silent
    e.preventDefault();
    // The destination as inert text (textContent, never parsed). A long or
    // hostile URL wraps inside the .modal-url box and, in the extreme, scrolls -
    // so it can never push the dialog out of bounds.
    const url = document.createElement("div");
    url.className = "modal-url";
    url.textContent = href;
    Modal({
      title: t("Open external link?"),
      body: url,
      confirmLabel: t("Open"),
      onConfirm: () => window.open(href, "_blank", "noopener,noreferrer"),
    });
  });
};
