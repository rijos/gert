// components/main/artifact-chips.js - the files this message created/edited, as
// clickable cards in the chat flow (the tool cards live behind the activity
// dropdown, so the chips are how a finished artifact stays one click away).
// Joined by name against state/artifacts - the same store a thread GET / live
// artifact event fills - so they work live and after reload without extra
// message state.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import * as artifactsState from "../../state/artifacts.js";
import type { Artifact } from "../../state/artifacts.js";
import type { Message as MessageRow } from "../../state/chat.js";
import * as ui from "../../state/ui.js";

const { div, span, button } = van.tags;

export const ArtifactChips = component({
  name: "artifact-chips",
  css: `
    /* artifact chips: the files this message produced, one click from the canvas */
    .artifact-strip {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin-top: 12px;
    }
    .artifact-chip {
      display: flex;
      align-items: center;
      gap: 8px;
      width: fit-content;
      max-width: 100%;
      padding: 8px 13px;
      background: var(--surface);
      border: 1px solid var(--line);
      border-radius: 10px;
      box-shadow: var(--lift);
      cursor: pointer;
      font-family: var(--mono);
      font-size: var(--fs-sm);
      color: var(--ink);
      transition: var(--t-fast);
    }
    /* colour-only hover: a transform here moved the chip under the cursor */
    .artifact-chip:hover {
      border-color: var(--coral);
      background: var(--coral-soft);
    }
    .artifact-chip svg {
      color: var(--coral);
      flex: none;
    }
    .artifact-chip .ac-name {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .artifact-chip .ac-hint {
      color: var(--ink-3);
      font-family: var(--sans);
      font-size: var(--fs-xs);
      flex: none;
    }
  `,
  // a binding so a newly-arrived artifact (matched by name against the store)
  // surfaces its chip live.
  view: (m: MessageRow) => () => {
    const names: string[] = [];
    const seen = new Set<string>();
    for (const c of m.tools) {
      if (
        (c.kind === "make_artifact" || c.kind === "edit_artifact") &&
        c.query &&
        !seen.has(c.query)
      ) {
        seen.add(c.query);
        names.push(c.query);
      }
    }
    const chips = names
      .map((n) => artifactsState.artifacts.find((a) => a.name === n))
      .filter((a): a is Artifact => a != null);
    if (!chips.length) return div();
    return div({ class: "artifact-strip" },
      ...chips.map((a) =>
        button({ class: "artifact-chip", title: "Open " + (a.name || "artifact") + " in the canvas", onclick: () => ui.openArtifact(a.id) },
          Icon("file", { size: 15, strokeWidth: 2 }),
          span({ class: "ac-name" }, a.name || "untitled"),
          span({ class: "ac-hint" }, "Open"),
        ),
      ),
    );
  },
});
