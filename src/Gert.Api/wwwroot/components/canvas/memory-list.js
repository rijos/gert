// components/canvas/memory-list.js - the project's memory entries, under the
// doc list in the knowledge view. Server returns them pinned-first, then
// newest (MemoryService.ListAsync); rows offer delete, the header an add
// modal (title + note + pin). Pinned entries ride every system prompt, so
// they wear the coral pin.
import van from "/lib/van.js";
import { reactive } from "/lib/van-x.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { Modal } from "../ui/modal.js";
import { Switch } from "../ui/switch.js";
import { fmtRelative } from "../../lib/format.js";
import * as memorySvc from "../../services/memory.js";
import * as chat from "../../state/chat.js";
import * as ui from "../../state/ui.js";
import { attempt } from "../../lib/action.js";
import { t } from "../../lib/i18n.js";

const { div, span, button, input, textarea, label } = van.tags;

export const MemoryList = component({
  name: "memory-list",
  css: `
    .memlist{flex:none; border-top:1px solid var(--line); padding:10px 12px 16px; max-height:40%; overflow-y:auto;}
    .mem-h{display:flex; align-items:center; gap:7px; padding:2px 6px 8px;}
    .mem-h .t{font-family:var(--mono); font-size:var(--fs-2xs); letter-spacing:.06em; text-transform:uppercase; color:var(--ink-3); flex:1;}
    .mem-add{display:flex; align-items:center; gap:4px; background:none; border:none; border-radius:var(--r-xs); color:var(--coral-deep); font-family:var(--sans); font-size:var(--fs-xs); font-weight:600; cursor:pointer; padding:3px 6px; transition:var(--t-fast);}
    .mem-add:hover{background:var(--coral-soft);}
    .mem-row{display:flex; align-items:center; gap:8px; padding:7px 8px; border-radius:var(--r-sm); transition:var(--t-fast);}
    .mem-row:hover{background:var(--surface-2);}
    .mem-row .pin{width:12px; flex:none; color:var(--coral-deep); display:grid; place-items:center;}
    .mem-row .mt{flex:1; min-width:0; font-size:var(--fs-sm); white-space:nowrap; overflow:hidden; text-overflow:ellipsis;}
    .mem-row .when{font-family:var(--mono); font-size:var(--fs-2xs); color:var(--ink-3); flex:none;}
    .mem-row .mem-del{display:none; align-items:center; justify-content:center; width:20px; height:20px; flex:none; background:none; border:none; border-radius:5px; color:var(--ink-3); cursor:pointer; padding:0; transition:var(--t-fast);}
    .mem-row:hover .mem-del{display:flex;}
    .mem-row .mem-del:hover{color:var(--brick); background:var(--surface-2);}
    .mem-empty{font-size:var(--fs-sm); color:var(--ink-3); padding:4px 8px;}
    .mem-note{width:100%; min-height:88px; resize:vertical;}
    .mem-pin-row{display:flex; align-items:center; gap:10px;}
    .mem-pin-row label{flex:1;}
  `,
  view: () => {
    // -- logic -----------------------------------
    const entries = reactive([]); // [{ id, title, pinned, updated_at }]

    const refresh = () =>
      attempt(async () => {
        const items = (await memorySvc.list()) || [];
        entries.length = 0;
        items.forEach((m) => entries.push(m));
      }, "Couldn't load memories");

    const promptAdd = () => {
      const titleEl = input({ placeholder: t("Title") });
      const noteEl = textarea({ class: "mem-note", placeholder: t("What should Gert remember?") });
      const pinned = van.state(false);
      Modal({
        title: t("New memory"),
        body: div(
          div({ class: "field" }, label(t("Title")), titleEl),
          div({ class: "field" }, label(t("Note")), noteEl),
          div(
            { class: "field mem-pin-row" },
            label(t("Pin - include in every chat")),
            Switch({ on: () => pinned.val, onToggle: () => (pinned.val = !pinned.val) }),
          ),
        ),
        confirmLabel: t("Save"),
        onConfirm: () => {
          const title = titleEl.value.trim();
          const content = noteEl.value.trim();
          if (!title || !content) return;
          attempt(async () => {
            await memorySvc.add({ title, content, pinned: pinned.val });
            refresh();
          }, "Couldn't save the memory");
        },
      });
    };

    const remove = (m) =>
      attempt(async () => {
        await memorySvc.remove(m.id);
        refresh();
      }, "Couldn't delete the memory");

    // -- content ---------------------------------
    const wrap = div(
      { class: "memlist" },
      div(
        { class: "mem-h" },
        span({ class: "t" }, () => `${t("Memories")} - ${entries.length}`),
        button(
          { class: "mem-add", onclick: promptAdd },
          Icon("plus", { size: 12, strokeWidth: 2.4 }),
          t("Add"),
        ),
      ),
      () =>
        entries.length
          ? div(
              ...entries.map((m) =>
                div(
                  { class: "mem-row" },
                  span(
                    { class: "pin", title: m.pinned ? "Pinned - rides every chat" : "" },
                    m.pinned ? Icon("sparkle", { size: 12, strokeWidth: 2 }) : span(),
                  ),
                  span({ class: "mt" }, m.title || "untitled"),
                  span({ class: "when" }, fmtRelative(m.updated_at)),
                  button(
                    {
                      class: "mem-del",
                      title: t("Delete memory"),
                      "aria-label": `Delete memory ${m.title}`,
                      onclick: () => remove(m),
                    },
                    Icon("trash", { size: 12, strokeWidth: 2 }),
                  ),
                ),
              ),
            )
          : div({ class: "mem-empty" }, t("Nothing remembered yet.")),
    );

    // (re)load whenever the knowledge view opens or the project changes -
    // scoped to `wrap` so the derive is pruned with the component (section 12).
    van.derive(
      () => {
        const visible = ui.showKnowledge.val;
        chat.activeProjectId.val; // dependency: project switches reload
        if (visible) refresh();
      },
      undefined,
      wrap,
    );

    return wrap;
  },
});
