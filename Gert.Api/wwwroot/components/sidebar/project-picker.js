// components/sidebar/project-picker.js — switch/create projects (configuration §8).
// Sits above the conversation list. Uses the ui/menu shell.
import van from "van";
import { Icon } from "../../icons/icons.js";
import { Menu } from "../ui/menu.js";
import { Modal } from "../ui/modal.js";
import * as chat from "../../state/chat.js";
import * as svc from "../../services/projects.js";
import { attempt } from "../../lib/action.js";

const { div, button, span, input } = van.tags;

export const ProjectPicker = () => {
  const open = van.state(false);

  const current = () =>
    chat.projects.find((p) => p.id === chat.activeProjectId.val)?.name ||
    (chat.activeProjectId.val === "default" ? "Default" : "Project");

  const trigger = button(
    {
      class: "project-btn",
      onclick: (e) => {
        e.stopPropagation();
        open.val = !open.val;
      },
    },
    Icon("book", { size: 14, strokeWidth: 2 }),
    span({ class: "pname" }, current),
    Icon("chevron", { size: 13, class: "chev", strokeWidth: 2.4 }),
  );

  const promptNew = () => {
    open.val = false;
    const nameInput = input({ class: "", placeholder: "Project name", style: "width:100%" });
    Modal({
      title: "New project",
      body: div({ class: "field" }, nameInput),
      confirmLabel: "Create",
      onConfirm: () => {
        const name = nameInput.value.trim();
        if (name) attempt(() => svc.create({ name }), "Couldn't create the project");
      },
    });
  };

  return Menu({
    wrapClass: "project-picker",
    open,
    trigger,
    children: [
      () =>
        div(
          ...chat.projects.map((p) =>
            div(
              {
                class: () =>
                  "p-item" + (chat.activeProjectId.val === p.id ? " sel" : ""),
                onclick: () => {
                  open.val = false;
                  attempt(() => svc.select(p.id), "Couldn't switch project");
                },
              },
              p.name,
            ),
          ),
        ),
      div({ class: "p-item p-new", onclick: promptNew }, "+ New project"),
    ],
  });
};
