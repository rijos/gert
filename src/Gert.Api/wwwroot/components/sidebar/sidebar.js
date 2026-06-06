// components/sidebar/sidebar.js — column container; composes the sidebar pieces.
// The pane-header (Brand) and the new-chat button are trivial single-use leaves,
// so they live here rather than in their own files. Responsive drawer behaviour
// comes from the .app state classes (app-shell.js) / layout.css.
import van from "van";
import { component } from "../../lib/component.js";
import { BrandMark, Icon } from "../../icons/icons.js";
import { ProjectPicker } from "./project-picker.js";
import { ConvoList } from "./convo-list.js";
import { UserChip } from "./user-chip.js";
import * as ui from "../../state/ui.js";
import * as chat from "../../state/chat.js";
import * as artifacts from "../../state/artifacts.js";
import { navigate } from "../../lib/router.js";

const { aside, div, h1, button } = van.tags;

// pane header: mark + title + drawer-close. The version line lives in settings.
const Brand = () =>
  div(
    { class: "brand" },
    div({ class: "mark" }, BrandMark()),
    h1("Gert"),
    button(
      {
        class: "ghost drawer-close",
        style: "margin-left:auto",
        title: "Close",
        onclick: ui.toggleNav,
      },
      Icon("close", { strokeWidth: 2.2 }),
    ),
  );

// start a fresh conversation and route home.
const NewChat = () =>
  button(
    {
      class: "newchat",
      onclick: () => {
        chat.newConversation();
        artifacts.clear();
        navigate("/");
      },
    },
    Icon("plus", { size: 15, strokeWidth: 2.2 }),
    "New chat",
  );

export const Sidebar = component({
  name: "sidebar",
  css: `
    .sidebar{background:var(--side-bg); border-right:1px solid var(--line); display:flex; flex-direction:column; overflow:hidden; min-width:0;}

    .brand{height:var(--head-h); flex:none; padding:0 20px; display:flex; align-items:center; gap:11px; border-bottom:1px solid var(--line);}
    .mark{width:30px; height:30px; flex:none; position:relative;}
    .mark svg{display:block;}
    .brand h1{font-family:var(--display); font-weight:600; font-size:25px; letter-spacing:-.01em; line-height:1;}

    .newchat{margin:4px 16px 14px; padding:10px 14px; border:1px solid var(--line); background:var(--surface); box-shadow:var(--lift);
      border-radius:var(--r); font-family:var(--sans); font-weight:600; font-size:13px; color:var(--ink);
      display:flex; align-items:center; gap:9px; cursor:pointer; transition:.16s; width:calc(100% - 32px);}
    .newchat:hover{border-color:var(--coral); color:var(--coral-deep); background:var(--coral-soft);}
    .newchat svg{width:15px; height:15px;}
  `,
  view: () =>
    aside(
      { class: "sidebar" },
      Brand(),
      ProjectPicker(),
      NewChat(),
      ConvoList(),
      UserChip(),
    ),
});
