// bootstrap: restore theme -> ensure session (PKCE / silent refresh) -> mount AppShell
// -> wire the router -> load initial data.
import van from "/lib/van.js";
import { mountRouter } from "./lib/router.js";
import { AppShell } from "./components/app-shell.js";
import { ChatPage } from "./pages/chat.js";
import { AdminUsersPage } from "./pages/admin/users.js";
import * as ui from "./state/ui.js";
import * as chat from "./state/chat.js";
import * as auth from "./services/auth.js";
import * as modelsSvc from "./services/models.js";
import * as toolsSvc from "./services/tools.js";
import * as projectsSvc from "./services/projects.js";
import * as conversationsSvc from "./services/conversations.js";

const boot = async () => {
  ui.restoreTheme(); // apply saved/OS theme before first paint
  ui.restorePanelWidth();

  await auth.ensureSession(); // PKCE / silent refresh (may navigate away)

  // the middle column the router swaps pages into; owned here, handed to AppShell.
  // The <main> landmark + #main target for the skip link (WCAG 2.4.1 / 1.3.1); tabindex=-1 so a
  // route change can move focus here programmatically without adding it to the tab order.
  const mainHost = van.tags.main({ class: "main", id: "main", tabindex: "-1" });

  // drop the SPA-fallback placeholder so its "Gert" text doesn't linger above the UI.
  document.getElementById("app")?.remove();
  van.add(document.body, AppShell(mainHost));

  // Title tracks the view (WCAG 2.4.2): admin section, the active conversation, or new chat.
  // index.html ships a static "Gert" for the pre-mount SPA-fallback; this takes over after boot.
  van.derive(() => {
    document.title = ui.adminRoute.val
      ? "Users - Gert"
      : chat.activeId.val
        ? `${chat.title.val} - Gert`
        : "New chat - Gert";
  });

  // After the first paint, a route change moves focus to the main region (WCAG 2.4.3) so a
  // keyboard/SR user lands in the new view instead of being stranded in the prior page's chrome.
  let firstRender = true;
  mountRouter({
    host: mainHost,
    render: (node) => {
      // Wire boundary: the router types a rendered page as `unknown`; every route
      // handler here returns a VanJS DOM Node (ChatPage/AdminUsersPage produce a
      // `div(...)`), so narrow to Node for replaceChildren.
      mainHost.replaceChildren(node as Node);
      if (!firstRender) {
        mainHost.focus();
      }

      firstRender = false;
    },
    routes: (route) => {
      // each handler flags whether we're on the admin route so the shell can
      // fold the canvas column away (ui.adminRoute -> .app.route-admin).
      route("/", () => {
        ui.adminRoute.val = false;
        return ChatPage({});
      });
      route("/c/:id", (p) => {
        ui.adminRoute.val = false;
        return ChatPage(p);
      });
      route("/admin/users", () => {
        ui.adminRoute.val = true;
        return AdminUsersPage();
      });
    },
  });

  // fire-and-forget: services update state, views react
  modelsSvc.loadWithUserDefault().catch(() => {});
  // The tool catalog is entitlement (token-scoped), like the model catalog - load
  // it once at boot; the popup reacts when it lands.
  toolsSvc.load().catch(() => {});
  projectsSvc.list().catch(() => {});
  conversationsSvc.list().catch(() => {});

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") ui.closeDrawers();
  });
};

boot();
