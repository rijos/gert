// components/sidebar/project-picker.js — switch/create projects (configuration §8).
// Sits above the conversation list. Uses the ui/menu shell.
import van from "van";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { Menu } from "../ui/menu.js";
import { Modal } from "../ui/modal.js";
import * as chat from "../../state/chat.js";
import * as svc from "../../services/projects.js";
import { navigate } from "../../lib/router.js";
import { attempt } from "../../lib/action.js";

const { div, button, span, input } = van.tags;

export const ProjectPicker = component({
  name: "project-picker",
  css: `
    .project-picker{margin:12px 16px 8px; position:relative;}
    .project-btn{width:calc(100% - 0px); display:flex; align-items:center; gap:8px; padding:8px 11px; border:1px solid var(--line); background:var(--inset); border-radius:var(--r-sm); cursor:pointer; font-family:var(--sans); font-weight:600; font-size:12.5px; color:var(--ink); transition:.14s;}
    .project-btn:hover{border-color:var(--accent); background:var(--accent-soft);}
    .project-btn .pname{flex:1; min-width:0; text-align:left; white-space:nowrap; overflow:hidden; text-overflow:ellipsis;}
    .project-btn .chev{width:13px; height:13px; color:var(--ink-faint); transition:.2s;}
    .project-picker.open .chev{transform:rotate(180deg);}
    .project-picker .menu{left:0; right:auto; width:232px; transform-origin:top left;}
    .project-picker.open .menu{opacity:1; transform:none; pointer-events:auto;}
    .p-item{padding:8px 10px; border-radius:var(--r-sm); cursor:pointer; transition:.12s; font-size:12.5px; font-weight:500;}
    .p-item:hover{background:var(--inset);}
    .p-item.sel{background:var(--accent-soft); color:var(--accent-deep); font-weight:600;}
    .p-new{border-top:1px solid var(--line); margin-top:5px; padding-top:8px; color:var(--accent-deep);}
  `,
  view: () => {
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
        if (name)
          attempt(async () => {
            const p = await svc.create({ name });
            if (p?.id) await svc.select(p.id); // land in the new project right away
          }, "Couldn't create the project");
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
                  attempt(async () => {
                    const recent = await svc.select(p.id);
                    navigate(recent ? "/c/" + recent.id : "/");
                  }, "Couldn't switch project");
                },
              },
              p.name,
            ),
          ),
        ),
      div({ class: "p-item p-new", onclick: promptNew }, "+ New project"),
    ],
  });
  },
});
