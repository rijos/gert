// components/main/avatar.js - brand-ish letter avatar for a source: the first
// letter of the registrable domain, tinted deterministically from the domain so
// the same source always matches. Used by the Sources card header stack and its
// per-source rows (sources.js).
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import type { Citation as CitationRow } from "../../state/chat.js";
import { domainOf, avatarHue } from "./message.helpers.js";

const { span } = van.tags;

export const Avatar = component({
  name: "avatar",
  css: `
    .s-avatar {
      width: 22px;
      height: 22px;
      border-radius: 7px;
      display: grid;
      place-items: center;
      font-size: var(--fs-xs);
      font-weight: 600;
      color: var(--ink);
      border: 1px solid var(--line);
      flex: none;
    }
  `,
  view: (c: CitationRow) => {
    const domain = domainOf(c.locator);
    const key = domain || c.label || "?";
    const parts = (domain || "").split(".");
    // split() never yields an empty array, so [length-2] (length>1) and [0] (the
    // else branch is the non-empty `key`) are both present; default-guarded below.
    const core = parts.length > 1 ? parts[parts.length - 2]! : key;
    return span({ class: "s-avatar", style: `background:color-mix(in srgb, hsl(${avatarHue(key)} 60% 50%) 22%, var(--surface-2))` },
      (core[0] || "?").toUpperCase(),
    );
  },
});
