// app.js - bootstrap: restore theme -> ensure session (PKCE / silent refresh)
// -> mount AppShell -> wire the router -> load initial data.
import van from "van";
import { mountRouter } from "./lib/router.js";
import { AppShell, mainHost } from "./components/app-shell.js";
import { ChatPage } from "./pages/chat.js";
import { AdminUsersPage } from "./pages/admin/users.js";
import * as ui from "./state/ui.js";
import * as auth from "./services/auth.js";
import * as modelsSvc from "./services/models.js";
import * as projectsSvc from "./services/projects.js";
import * as conversationsSvc from "./services/conversations.js";

const boot = async () => {
  ui.restoreTheme(); // apply saved/OS theme before first paint
  ui.restorePanelWidth(); // apply saved canvas-panel width

  await auth.ensureSession(); // PKCE / silent refresh (may navigate away)

  // mount the shell once - drop the SPA-fallback placeholder so its "Gert"
  // text doesn't linger above the mounted UI.
  document.getElementById("app")?.remove();
  van.add(document.body, AppShell());

  // wire the router: it renders the matched page into the main region.
  mountRouter({
    host: mainHost,
    render: (node) => {
      mainHost.replaceChildren(node);
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

  // load initial data (services update state; views react)
  modelsSvc.loadWithUserDefault().catch(() => {});
  projectsSvc.list().catch(() => {});
  conversationsSvc.list().catch(() => {});

  // global esc closes mobile drawers
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") ui.closeDrawers();
  });
};

boot();
