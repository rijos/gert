// components/sidebar/sidebar.js — column container; composes the sidebar pieces.
// Responsive drawer behaviour comes from the .app state classes (app-shell.js).
import van from "van";
import { Brand } from "./brand.js";
import { ProjectPicker } from "./project-picker.js";
import { NewChat } from "./new-chat.js";
import { ConvoList } from "./convo-list.js";
import { UserChip } from "./user-chip.js";

const { aside } = van.tags;

export const Sidebar = () =>
  aside(
    { class: "sidebar" },
    Brand(),
    ProjectPicker(),
    NewChat(),
    ConvoList(),
    UserChip(),
  );
