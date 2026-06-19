// bootstrap: restore theme -> ensure session (PKCE / silent refresh) -> mount AppShell
// -> wire the router -> load initial data.
import van from "/lib/van.js";
import { mountRouter } from "./lib/router.js";
import { AppShell } from "./components/app-shell.js";
import { ChatPage } from "./pages/chat.js";
import { AdminUsersPage } from "./pages/admin/users.js";
import * as ui from "./state/ui.js";
import * as auth from "./services/auth.js";
import * as modelsSvc from "./services/models.js";
import * as projectsSvc from "./services/projects.js";
import * as conversationsSvc from "./services/conversations.js";

const boot = async () => {
  ui.restoreTheme(); // apply saved/OS theme before first paint
  ui.restorePanelWidth();

  await auth.ensureSession(); // PKCE / silent refresh (may navigate away)

  // the middle column the router swaps pages into; owned here, handed to AppShell.
  const mainHost = van.tags.div({ class: "main" });

  // drop the SPA-fallback placeholder so its "Gert" text doesn't linger above the UI.
  document.getElementById("app")?.remove();
  van.add(document.body, AppShell(mainHost));

  mountRouter({
    host: mainHost,
    render: (node) => {
      // Wire boundary: the router types a rendered page as `unknown`; every route
      // handler here returns a VanJS DOM Node (ChatPage/AdminUsersPage produce a
      // `div(...)`), so narrow to Node for replaceChildren.
      mainHost.replaceChildren(node as Node);
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
  projectsSvc.list().catch(() => {});
  conversationsSvc.list().catch(() => {});

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") ui.closeDrawers();
  });
};

boot();
