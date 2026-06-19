// pages/admin/users.js - /admin/users - admin-only user list, plus the
// model-prompt inspector (the system prompt + full tool specs as advertised
// to the model - GET /api/admin/system-prompt).
// Reads via services/admin (server enforces the Admin policy). Non-admins get
// a notice; the real boundary is server-side.
import van from "/lib/van.js";
import { Modal } from "../../components/ui/modal.js";
import { toast } from "../../components/ui/toast.js";
import { attempt } from "../../lib/action.js";
import { fmtBytes, fmtRelative } from "../../lib/format.js";
import * as admin from "../../services/admin.js";
import type { WireSystemPrompt, WireUserSummary } from "../../services/wire.js";
import * as auth from "../../state/auth.js";

const { div, h1, h2, p, a, pre, span, table, thead, tbody, tr, th, td, button } = van.tags;

// The admin-only wire rows (GET /api/admin/...) come straight off the typed admin service:
// WireSystemPrompt (system prompt + WireToolSpec[]) and WireUserSummary[].

// pretty-print a tool's JSON-schema string; a malformed one shows verbatim
// (this page exists to reveal exactly what the model gets, warts included)
const fmtSchema = (s: string) => {
  try {
    return JSON.stringify(JSON.parse(s), null, 2);
  } catch {
    return s;
  }
};

// the system prompt + per-tool specs, loaded once per page visit
const PromptSection = () => {
  const snapshot = van.state<WireSystemPrompt | null>(null);
  const failed = van.state(false);
  attempt(async () => {
    snapshot.val = await admin.getSystemPrompt();
    return true;
  }, "Couldn't load the system prompt").then((ok) => {
    if (!ok) failed.val = true;
  });

  return div(
    h2({ class: "prompt-h" }, "Model prompt"),
    p(
      { class: "sub" },
      "What every turn sends upstream: the built-in system prompt, then each tool exactly as advertised. " +
        "Per-project pinned instructions append to the prompt at send time and are per-user data, so they are not shown here.",
    ),
    () =>
      failed.val
        ? p("Couldn't load the system prompt.")
        : snapshot.val === null
          ? p("Loading...")
          : div(
              h2({ class: "prompt-sub" }, "System prompt"),
              pre({ class: "prompt-block" }, snapshot.val.system_prompt || "(empty)"),
              h2({ class: "prompt-sub" }, `Tools (${snapshot.val.tools.length})`),
              ...snapshot.val.tools.map((t) =>
                div(
                  { class: "tool-spec" },
                  div(
                    { class: "tool-head" },
                    span({ class: "tool-name" }, t.name),
                    span({ class: "tool-id" }, t.id),
                  ),
                  p({ class: "tool-desc" }, t.description),
                  pre({ class: "prompt-block" }, fmtSchema(t.parameters_schema)),
                ),
              ),
            ),
  );
};

export const AdminUsersPage = () => {
  const users = van.state<WireUserSummary[] | null>(null); // null = loading, [] = genuinely empty
  const failed = van.state(false); // a failed load is NOT an empty list (section 8)
  const load = async () => {
    failed.val = false;
    const ok = await attempt(async () => {
      users.val = await admin.listUsers();
      return true;
    }, "Couldn't load users");
    if (!ok) failed.val = true;
  };
  load();

  const confirmDelete = (u: WireUserSummary) =>
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
      // route escape - admin has no topbar, so the page carries its own way home
      a({ class: "backlink", href: "/", "data-link": "" }, "Back to chat"),
      h1("Users"),
      p({ class: "sub" }, "User data folders (admin only)."),
      () =>
        !auth.isAdmin.val
          ? p("You do not have admin access.")
          : failed.val
            ? div(
                p("Couldn't load users."),
                button({ class: "btn secondary", onclick: load }, "Retry"),
              )
            : users.val === null
            ? p("Loading...")
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
                      td(u.size != null ? fmtBytes(u.size) : "-"),
                      // human-relative, with the precise ISO on the tooltip
                      td(
                        { title: u.last_active || "" },
                        u.last_active ? fmtRelative(u.last_active) : "-",
                      ),
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
      // the model-prompt inspector rides the same admin gate
      () => (auth.isAdmin.val ? PromptSection() : div()),
    ),
  );
};
