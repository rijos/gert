// pages/admin/users.js — /admin/users — admin-only user list.
// Reads via services/admin (server enforces the Admin policy). Non-admins get
// a notice; the real boundary is server-side.
import van from "van";
import { Modal } from "../../components/ui/modal.js";
import { toast } from "../../components/ui/toast.js";
import * as admin from "../../services/admin.js";
import * as auth from "../../state/auth.js";

const { div, h1, p, table, thead, tbody, tr, th, td, button } = van.tags;

const fmtSize = (b) =>
  b > 1_073_741_824
    ? (b / 1_073_741_824).toFixed(1) + " GB"
    : (b / 1_048_576).toFixed(1) + " MB";

export const AdminUsersPage = () => {
  const users = van.state(null);
  const load = () =>
    admin
      .listUsers()
      .then((u) => (users.val = u || []))
      .catch(() => (users.val = []));
  load();

  const confirmDelete = (u) =>
    Modal({
      title: "Remove user?",
      body: `This runs rm -rf on ${u.username || u.key}'s data folder. This cannot be undone.`,
      confirmLabel: "Delete",
      onConfirm: () =>
        admin
          .removeUser(u.key)
          .then(() => {
            toast("User removed", "ok");
            load();
          })
          .catch(() => toast("Delete failed", "err")),
    });

  return div(
    { class: "page" },
    div(
      { class: "page-inner" },
      h1("Users"),
      p({ class: "sub" }, "User data folders (admin only)."),
      () =>
        !auth.isAdmin.val
          ? p("You do not have admin access.")
          : users.val === null
            ? p("Loading…")
            : table(
                { class: "utable" },
                thead(
                  tr(
                    th("User"),
                    th("Docs"),
                    th("Size"),
                    th("Last active"),
                    th(""),
                  ),
                ),
                tbody(
                  ...users.val.map((u) =>
                    tr(
                      td(u.username || u.key),
                      td(String(u.document_count ?? 0)),
                      td(u.size != null ? fmtSize(u.size) : "—"),
                      td(u.last_active || "—"),
                      td(
                        button(
                          { class: "btn secondary", onclick: () => confirmDelete(u) },
                          "Delete",
                        ),
                      ),
                    ),
                  ),
                ),
              ),
    ),
  );
};
