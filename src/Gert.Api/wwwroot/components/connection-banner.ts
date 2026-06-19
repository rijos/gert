// components/connection-banner.js - a fixed top bar shown while the connection is
// degraded (state/health.js): the server is unreachable and a refresh may recover.
// Above all layers (z-90, the top of the section-10 ladder).
import van from "/lib/van.js";
import { component } from "../lib/component.js";
import * as health from "../state/health.js";
import { t } from "../lib/i18n.js";

const { div, span, button } = van.tags;

export const ConnectionBanner = component({
  name: "connection-banner",
  css: `
    .conn-banner {
      position: fixed;
      top: 0;
      left: 0;
      right: 0;
      z-index: 90;
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 12px;
      padding: 8px 16px;
      background: var(--fail-bg);
      color: var(--fail-fg);
      border-bottom: 1px solid var(--brick);
      font-size: var(--fs-sm);
      box-shadow: var(--lift);
    }

    .conn-banner .cb-refresh {
      border: 1px solid var(--brick);
      border-radius: var(--r-sm);
      background: none;
      color: var(--fail-fg);
      font-size: var(--fs-sm);
      font-weight: 600;
      padding: 2px 10px;
      cursor: pointer;
    }
  `,
  view: () => () =>
    health.degraded.val
      ? div({ class: "conn-banner" },
          span(t("Connection problems - some actions may fail.")),
          button({ class: "cb-refresh", onclick: () => location.reload() }, t("Refresh")),
        )
      : null,
});
