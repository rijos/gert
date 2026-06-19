// components/sidebar/sidebar.js - column container; composes the sidebar pieces.
// The pane-header (Brand) and the new-chat button are trivial single-use leaves,
// so they live here rather than in their own files. Responsive drawer behaviour
// comes from the .app state classes (app-shell.js) / layout.css.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { BrandMark, Icon } from "../../icons/icons.js";
import { ProjectPicker } from "./project-picker.js";
import { ConvoList } from "./convo-list.js";
import { UserChip } from "./user-chip.js";
import { openSearch } from "../search-overlay.js";
import { t } from "../../lib/i18n.js";
import * as ui from "../../state/ui.js";
import * as chat from "../../state/chat.js";
import * as chatSvc from "../../services/chat.js";
import * as artifacts from "../../state/artifacts.js";
import { navigate } from "../../lib/router.js";

const { aside, div, h1, button } = van.tags;

// pane header. The version line lives in settings, not here.
const Brand = () =>
  div({ class: "brand" },
    div({ class: "mark" }, BrandMark()),
    h1("Gert"),
    button({ class: "ghost drawer-close", style: "margin-left:auto", title: t("Close"), onclick: ui.toggleNav },
      Icon("close", { strokeWidth: 2.2 }),
    ),
  );

const NewChat = () =>
  button(
    {
      class: "newchat",
      onclick: () => {
        chatSvc.detach(); // leaving a mid-stream thread - unpin the composer
        chat.newConversation();
        artifacts.clear();
        navigate("/");
      },
    },
    Icon("plus", { size: 15, strokeWidth: 2.2 }),
    t("New chat"),
  );

export const Sidebar = component({
  name: "sidebar",
  css: `
    /* paper grain rides the pane background (tokens.css --grain-img) */
    .sidebar {
      background: var(--side-bg);
      background-image: var(--grain-img);
      background-size: 18px 18px;
      border-right: 1px solid var(--line);
      display: flex;
      flex-direction: column;
      overflow: hidden;
      min-width: 0;
    }

    .brand {
      height: var(--head-h);
      flex: none;
      padding: 0 20px;
      display: flex;
      align-items: center;
      gap: 11px;
      border-bottom: 1px solid var(--line);
    }

    .mark {
      width: 30px;
      height: 30px;
      flex: none;
      position: relative;
    }

    .mark svg {
      display: block;
    }

    .brand h1 {
      font-family: var(--display);
      font-weight: 600;
      font-size: var(--fs-xl);
      letter-spacing: -.01em;
      line-height: 1;
    }

    /* the sidebar's one clear call to action: a filled accent button (same
       --coral-deep/--on-accent pairing as .btn - AA in both themes) */
    .newchat {
      margin: 4px 16px 14px;
      padding: 11px 14px;
      border: 1px solid var(--coral-deep);
      background: var(--coral-deep);
      border-radius: var(--r);
      font-family: var(--sans);
      font-weight: 600;
      font-size: var(--fs-md);
      color: var(--on-accent);
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 9px;
      cursor: pointer;
      transition: var(--t-fast);
      width: calc(100% - 32px);
      box-shadow: var(--lift);
    }

    /* colour-only hover: a transform here moved the button under the cursor */
    .newchat:hover {
      background: var(--coral);
      border-color: var(--coral);
    }

    .newchat svg {
      width: 15px;
      height: 15px;
    }

    /* quiet full-screen-search entries under the call to action */
    .side-search {
      display: flex;
      gap: 6px;
      margin: 0 16px 12px;
    }

    .side-search button {
      flex: 1;
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 7px;
      padding: 7px 10px;
      background: none;
      border: 1px solid var(--line);
      border-radius: var(--r-sm);
      font-family: var(--sans);
      font-size: var(--fs-xs);
      font-weight: 500;
      color: var(--ink-2);
      cursor: pointer;
      transition: var(--t-fast);
    }

    .side-search button:hover {
      border-color: var(--coral);
      color: var(--coral-deep);
      background: var(--coral-soft);
    }

    .side-search svg {
      width: 12px;
      height: 12px;
    }
  `,
  view: () =>
    aside({ class: "sidebar" },
      Brand(),
      ProjectPicker(),
      NewChat(),
      div({ class: "side-search" },
        button({ onclick: () => openSearch("chats"), title: t("Search all chats") },
          Icon("search", { size: 12, strokeWidth: 2.2 }),
          t("Chats"),
        ),
        button({ onclick: () => openSearch("projects"), title: t("Search all projects") },
          Icon("search", { size: 12, strokeWidth: 2.2 }),
          t("Projects"),
        ),
      ),
      ConvoList(),
      UserChip(),
    ),
});
