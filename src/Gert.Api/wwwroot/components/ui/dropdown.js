// components/ui/dropdown.js - generic select-style dropdown on the ui/menu shell.
// Owns the trigger button (label + chevron), selected-row highlighting, an
// optional filter input, and the open-state reveal - so consumers only bring
// data and (optionally) positioning/skin overrides via `wrapClass`.
//
// Dropdown({ items, value, onSelect?, placeholder?, icon?, header?, footer?,
//            searchable?, wrapClass?, renderItem? })
//   items       - [{ value, label }] or fn () => [...] for reactive lists.
//   value       - van.state holding the selected item's value.
//   onSelect    - fn(item); replaces the default behavior (value.val = item.value).
//   placeholder - string or fn; shown when no item matches `value`.
//   icon        - optional leading icon name on the trigger.
//   header      - optional .menu-h text above the list.
//   footer      - node or fn(close) appended after the list (e.g. "+ New ...").
//   searchable  - show a filter input at the top of the menu.
//   renderItem  - fn(item) => node for custom row content.
import van from "van";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { Menu } from "./menu.js";

const { div, button, span, input } = van.tags;

export const Dropdown = component({
  name: "dropdown",
  css: `
    .dd{position:relative;}
    .dd-btn{display:flex; align-items:center; gap:8px; width:100%; padding:9px 12px; border:1px solid var(--line); background:var(--surface); border-radius:var(--r-sm); cursor:pointer; font-family:var(--sans); font-weight:500; font-size:var(--fs-md); color:var(--ink); transition:var(--t-fast); text-align:left;}
    .dd-btn:hover{border-color:var(--coral); background:var(--coral-soft);}
    .dd-btn .dd-label{flex:1; min-width:0; white-space:nowrap; overflow:hidden; text-overflow:ellipsis;}
    .dd-btn .chev{width:13px; height:13px; color:var(--ink-3); transition:var(--t-slow) var(--ease); flex:none;}
    .dd.open .chev{transform:rotate(180deg);}
    .dd .menu{left:0; right:auto; width:100%; min-width:200px; transform-origin:top left;}
    .dd.open .menu{opacity:1; transform:none; pointer-events:auto;}
    /* border tint on focus is a bonus - the global :focus-visible ring stays */
    .dd-search{width:100%; font-family:var(--sans); font-size:var(--fs-md); color:var(--ink); background:var(--surface-2); border:1px solid var(--line); border-radius:var(--r-sm); padding:7px 10px; margin-bottom:5px;}
    .dd-search:focus{border-color:var(--coral);}
    .dd-item{padding:var(--sp-2) var(--sp-3); border-radius:var(--r-sm); cursor:pointer; transition:var(--t-fast); font-size:var(--fs-md); font-weight:500;}
    .dd-item:hover{background:var(--surface-2);}
    .dd-item.sel{background:var(--coral-soft); color:var(--coral-deep); font-weight:600;}
    .dd-empty{padding:8px 10px; font-size:var(--fs-sm); color:var(--ink-3);}
  `,
  view: ({
    items,
    value,
    onSelect,
    placeholder = "Select...",
    icon = null,
    header = null,
    footer = null,
    searchable = false,
    wrapClass = "",
    renderItem = null,
  } = {}) => {
    // -- logic -----------------------------------
    const open = van.state(false);
    const query = van.state("");
    const close = () => (open.val = false);

    const list = () => (typeof items === "function" ? items() : items) || [];
    const filtered = () => {
      const q = query.val.trim().toLowerCase();
      return q
        ? list().filter((i) => String(i.label).toLowerCase().includes(q))
        : list();
    };
    const label = () =>
      list().find((i) => i.value === value.val)?.label ??
      (typeof placeholder === "function" ? placeholder() : placeholder);

    const pick = (item) => {
      close();
      if (onSelect) onSelect(item);
      else value.val = item.value;
    };

    const search = searchable
      ? input({
          class: "dd-search",
          placeholder: "Search...",
          oninput: (e) => (query.val = e.target.value),
          onkeydown: (e) => {
            if (e.key === "Escape") close();
            if (e.key === "Enter") {
              const first = filtered()[0];
              if (first) pick(first);
            }
          },
        })
      : null;
    if (search)
      van.derive(() => {
        // focus the filter on open; clear it on close
        if (open.val) setTimeout(() => search.focus(), 0);
        else (query.val = ""), (search.value = "");
      });

    // -- content ---------------------------------
    const trigger = button(
      {
        class: "dd-btn",
        type: "button",
        onclick: (e) => {
          e.stopPropagation();
          open.val = !open.val;
        },
      },
      icon ? Icon(icon, { size: 14, strokeWidth: 2 }) : null,
      span({ class: "dd-label" }, label),
      Icon("chevron", { size: 13, class: "chev", strokeWidth: 2.4 }),
    );

    const Row = (item) =>
      div(
        {
          class: () => "dd-item" + (value.val === item.value ? " sel" : ""),
          onclick: () => pick(item),
        },
        renderItem ? renderItem(item) : item.label,
      );

    return Menu({
      wrapClass: ("dd " + wrapClass).trim(),
      open,
      trigger,
      children: [
        header ? div({ class: "menu-h" }, header) : null,
        search,
        () => {
          const rows = filtered();
          return rows.length
            ? div(...rows.map(Row))
            : div({ class: "dd-empty" }, "No matches");
        },
        typeof footer === "function" ? footer(close) : footer,
      ],
    });
  },
});
