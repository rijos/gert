// components/sidebar/project-picker.js — switch/create projects (configuration §8).
// Sits above the conversation list. A skinned ui/dropdown (searchable) with a
// "+ New project" footer that opens the create modal.
import van from "van";
import { component } from "../../lib/component.js";
import { Dropdown } from "../ui/dropdown.js";
import { Modal } from "../ui/modal.js";
import * as chat from "../../state/chat.js";
import * as svc from "../../services/projects.js";
import { navigate } from "../../lib/router.js";
import { attempt } from "../../lib/action.js";

const { div, input } = van.tags;

export const ProjectPicker = component({
  name: "project-picker",
  css: `
    .project-picker{margin:12px 16px 8px;}
    .project-picker .dd-btn{padding:8px 11px; border-color:var(--line); background:var(--inset); font-weight:600; font-size:12.5px;}
    .project-picker .dd-btn:hover{border-color:var(--accent); background:var(--accent-soft);}
    .project-picker .dd-item{font-size:12.5px;}
    .p-new{padding:8px 10px; border-radius:var(--r-sm); cursor:pointer; transition:.12s; font-size:12.5px; font-weight:500; border-top:1px solid var(--line); margin-top:5px; color:var(--accent-deep);}
    .p-new:hover{background:var(--inset);}
  `,
  view: () => {
    const promptNew = () => {
      const nameInput = input({ class: "", placeholder: "Project name", style: "width:100%" });
      Modal({
        title: "New project",
        body: div({ class: "field" }, nameInput),
        confirmLabel: "Create",
        onConfirm: () => {
          const name = nameInput.value.trim();
          if (name)
            attempt(async () => {
              const p = await svc.create({ name });
              if (p?.id) await svc.select(p.id); // land in the new project right away
            }, "Couldn't create the project");
        },
      });
    };

    return Dropdown({
      wrapClass: "project-picker",
      icon: "book",
      searchable: true,
      items: () => chat.projects.map((p) => ({ value: p.id, label: p.name })),
      value: chat.activeProjectId,
      placeholder: () =>
        chat.activeProjectId.val === "default" ? "Default" : "Project",
      onSelect: (item) =>
        attempt(async () => {
          const recent = await svc.select(item.value);
          navigate(recent ? "/c/" + recent.id : "/");
        }, "Couldn't switch project"),
      footer: (close) =>
        div(
          {
            class: "p-new",
            onclick: () => {
              close();
              promptNew();
            },
          },
          "+ New project",
        ),
    });
  },
});
