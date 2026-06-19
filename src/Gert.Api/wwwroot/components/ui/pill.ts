// components/ui/pill.js - status pill (ready / proc / fail).
import van from "/lib/van.js";
import { component } from "../../lib/component.js";

const { span } = van.tags;

// String-keyed so the tolerant `LABELS[kind]` lookup type-checks (callers may
// pass a kind outside the known set; the `|| kind` fallback covers it).
const LABELS: Record<string, string> = { ready: "Ready", proc: "Processing", fail: "Failed" };

export const Pill = component({
  name: "pill",
  css: `
    .pill {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      padding: 3px 7px;
      border-radius: 20px;
      font-weight: 500;
      display: flex;
      align-items: center;
      gap: 4px;
      flex: none;
    }

    .pill .pd {
      width: 5px;
      height: 5px;
      border-radius: 50%;
    }

    .pill.ready {
      background: var(--coral-soft);
      color: var(--coral-deep);
    }
    .pill.ready .pd {
      background: var(--coral);
    }

    .pill.proc {
      background: var(--proc-bg);
      color: var(--proc-fg);
    }
    .pill.proc .pd {
      background: var(--amber);
      animation: pulse 1.1s infinite;
    }

    .pill.fail {
      background: var(--fail-bg);
      color: var(--fail-fg);
    }
    .pill.fail .pd {
      background: var(--brick);
    }
  `,
  view: ({ kind = "ready", label }: { kind?: string; label?: string } = {}) =>
    span(
      { class: "pill " + kind },
      // the colored dot is decorative; the text label carries the status (not color alone).
      span({ class: "pd", "aria-hidden": "true" }),
      label || LABELS[kind] || kind,
    ),
});
