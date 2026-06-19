// switch/create/manage projects (configuration section 8). A skinned, searchable
// ui/dropdown with a "+ New project" footer; each row carries hover rename/delete
// affordances (the API's PATCH/DELETE).
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Dropdown } from "../ui/dropdown.js";
import { Modal } from "../ui/modal.js";
import { Icon } from "../../icons/icons.js";
import * as chat from "../../state/chat.js";
import * as svc from "../../services/projects.js";
import { navigate } from "../../lib/router.js";
import { attempt } from "../../lib/action.js";
import { t } from "../../lib/i18n.js";

const { div, input, span, button, p } = van.tags;

// A dropdown row: the row callbacks (rename/delete/select) read these fields back off it.
interface DdItem {
  value: string;
  label: string;
}

export const ProjectPicker = component({
  name: "project-picker",
  css: `
    .project-picker {
      margin: 12px 16px 8px;
    }

    .project-picker .dd-btn {
      padding: 8px 11px;
      border-color: var(--line);
      background: var(--surface-2);
      font-weight: 600;
      font-size: var(--fs-sm);
    }

    .project-picker .dd-btn:hover {
      border-color: var(--coral);
      background: var(--coral-soft);
    }

    .project-picker .dd-item {
      font-size: var(--fs-sm);
    }

    .p-new {
      display: block;
      width: 100%;
      text-align: left;
      background: none;
      border: none;
      border-top: 1px solid var(--line);
      font-family: inherit;
      padding: var(--sp-2) var(--sp-3);
      border-radius: var(--r-sm);
      cursor: pointer;
      transition: var(--t-fast);
      font-size: var(--fs-sm);
      font-weight: 500;
      margin-top: 5px;
      color: var(--coral-deep);
    }

    .p-new:hover {
      background: var(--surface-2);
    }

    /* row management: actions reveal on row hover (mirrors convo-item).
       opacity, NOT display - the buttons keep their layout slot, so revealing
       them never resizes the row or re-truncates the name. */
    .p-row {
      display: flex;
      align-items: center;
      gap: 6px;
      min-width: 0;
    }

    .p-row .p-name {
      flex: 1;
      min-width: 0;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }

    .p-act {
      opacity: 0;
      display: flex;
      align-items: center;
      justify-content: center;
      width: 24px;
      height: 24px;
      flex: none;
      background: none;
      border: none;
      border-radius: 5px;
      color: var(--ink-3);
      cursor: pointer;
      padding: 0;
      transition: var(--t-fast);
    }

    .dd-item:hover .p-act,.p-act:focus-visible {
      opacity: 1;
    }

    .p-act:hover {
      color: var(--coral-deep);
      background: var(--coral-soft);
    }

    .p-act.danger:hover {
      color: var(--brick);
      background: var(--surface-2);
    }
  `,
  // The explicit return type names the view-model so the setup overload resolves
  // unambiguously (a large view can otherwise tip overload inference into widening the bag
  // to `any`).
  setup: (): {
    promptNew: () => void;
    promptRename: (item: DdItem) => void;
    promptDelete: (item: DdItem) => void;
    onSelect: (item: DdItem) => void;
  } => {
    const promptNew = () => {
      const nameInput = input({ class: "", placeholder: t("Project name"), style: "width:100%" });
      Modal({
        title: t("New project"),
        body: div({ class: "field" }, nameInput),
        confirmLabel: t("Create"),
        onConfirm: () => {
          const name = nameInput.value.trim();
          if (name)
            attempt(async () => {
              const p = await svc.create({ name });
              if (p.id) await svc.select(p.id); // land in the new project right away
            }, "Couldn't create the project");
        },
      });
    };

    const promptRename = (item: DdItem) => {
      const nameInput = input({ placeholder: t("Project name"), value: item.label, style: "width:100%" });
      Modal({
        title: t("Rename project"),
        body: div({ class: "field" }, nameInput),
        confirmLabel: t("Rename"),
        onConfirm: () => {
          const name = nameInput.value.trim();
          if (name && name !== item.label)
            attempt(() => svc.rename(item.value, name), "Couldn't rename the project");
        },
      });
    };

    const promptDelete = (item: DdItem) => {
      Modal({
        title: t("Delete project"),
        body: p(
          `Delete "${item.label}" with all its conversations, documents and ` +
            "memories? This cannot be undone.",
        ),
        confirmLabel: t("Delete"),
        onConfirm: () =>
          attempt(async () => {
            await svc.remove(item.value);
            navigate("/");
          }, "Couldn't delete the project"),
      });
    };

    const onSelect = (item: DdItem) =>
      attempt(async () => {
        const recent = await svc.select(item.value);
        navigate(recent ? "/c/" + recent.id : "/");
      }, "Couldn't switch project");

    return { promptNew, promptRename, promptDelete, onSelect };
  },
  view: ({ promptNew, promptRename, promptDelete, onSelect }) =>
    Dropdown({
      wrapClass: "project-picker",
      icon: "book",
      searchable: true,
      items: () => chat.projects.map((p) => ({ value: p.id, label: p.name })),
      value: chat.activeProjectId,
      placeholder: () =>
        chat.activeProjectId.val === "default" ? t("Default") : t("Project"),
      onSelect,
      renderItem: (item: DdItem) =>
        div({ class: "p-row" },
          span({ class: "p-name" }, item.label),
          button({ class: "p-act", title: t("Rename project"), "aria-label": `Rename project ${item.label}`, onclick: (e: Event) => { e.stopPropagation(); promptRename(item); } },
            Icon("edit", { size: 12, strokeWidth: 2 }),
          ),
          // "default" always exists - DELETE empties it rather than removing
          // the folder, so the affordance reads wrong; hide it there.
          item.value === "default"
            ? null
            : button({ class: "p-act danger", title: t("Delete project"), "aria-label": `Delete project ${item.label}`, onclick: (e: Event) => { e.stopPropagation(); promptDelete(item); } },
                Icon("trash", { size: 12, strokeWidth: 2 }),
              ),
        ),
      footer: (close: () => void) =>
        button({ class: "p-new", type: "button", onclick: () => { close(); promptNew(); } },
          t("+ New project"),
        ),
    }),
});
