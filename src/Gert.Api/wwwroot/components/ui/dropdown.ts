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
import van from "/lib/van.js";
import type { State, ChildDom } from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { Menu } from "./menu.js";

const { div, button, span, input } = van.tags;

interface DropdownItem {
  value: string;
  label: string;
}

interface DropdownProps {
  items: DropdownItem[] | (() => DropdownItem[]);
  value: State<string>;
  onSelect?: (item: DropdownItem) => void;
  placeholder?: string | (() => string);
  icon?: string | null;
  header?: ChildDom;
  // node, or fn(close) => node (typeof check narrows the function branch).
  footer?: Node | ((close: () => void) => ChildDom) | null;
  searchable?: boolean;
  wrapClass?: string;
  renderItem?: ((item: DropdownItem) => ChildDom) | null;
  // Names the trigger for AT (WCAG 4.1.2) - a visible <label> can't associate with this
  // custom widget, so the field label is passed in instead.
  ariaLabel?: string;
}

export const Dropdown = component({
  name: "dropdown",
  css: `
    .dd {
      position: relative;
    }

    .dd-btn {
      display: flex;
      align-items: center;
      gap: 8px;
      width: 100%;
      padding: 9px 12px;
      border: 1px solid var(--line);
      background: var(--surface);
      border-radius: var(--r-sm);
      cursor: pointer;
      font-family: var(--sans);
      font-weight: 500;
      font-size: var(--fs-md);
      color: var(--ink);
      transition: var(--t-fast);
      text-align: left;
    }
    .dd-btn:hover {
      border-color: var(--coral);
      background: var(--coral-soft);
    }
    .dd-btn .dd-label {
      flex: 1;
      min-width: 0;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .dd-btn .chev {
      width: 13px;
      height: 13px;
      color: var(--ink-3);
      transition: var(--t-slow) var(--ease);
      flex: none;
    }

    .dd.open .chev {
      transform: rotate(180deg);
    }

    .dd .menu {
      left: 0;
      right: auto;
      width: 100%;
      min-width: 200px;
      transform-origin: top left;
    }
    .dd.open .menu {
      opacity: 1;
      visibility: visible;
      transform: none;
      pointer-events: auto;
    }

    /* border tint on focus is a bonus - the global :focus-visible ring stays */
    .dd-search {
      width: 100%;
      font-family: var(--sans);
      font-size: var(--fs-md);
      color: var(--ink);
      background: var(--surface-2);
      border: 1px solid var(--line);
      border-radius: var(--r-sm);
      padding: 7px 10px;
      margin-bottom: 5px;
    }
    .dd-search:focus {
      border-color: var(--coral);
    }

    .dd-item {
      padding: var(--sp-2) var(--sp-3);
      border-radius: var(--r-sm);
      cursor: pointer;
      transition: var(--t-fast);
      font-size: var(--fs-md);
      font-weight: 500;
    }
    .dd-item:hover {
      background: var(--surface-2);
    }
    .dd-item.sel {
      background: var(--coral-soft);
      color: var(--coral-deep);
      font-weight: 600;
    }

    .dd-empty {
      padding: 8px 10px;
      font-size: var(--fs-sm);
      color: var(--ink-3);
    }
  `,
  // logic: open/query state + the pure derives (list/filtered/label) and the
  // pick/close handlers. The focus-on-open van.derive is DOM-scoped (it reads the
  // `search` input built in `view`) so it STAYS in `view` (lifetime rule); setup
  // holds only state + pure, non-reactive values.
  // `= {} as DropdownProps` keeps the no-arg default while typing `items`/`value`
  // as required (so `list()`/`value.val` type-check).
  setup: ({
    items,
    value,
    onSelect,
    placeholder = "Select...",
  }: DropdownProps = {} as DropdownProps) => {
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

    const pick = (item: DropdownItem) => {
      close();
      if (onSelect) onSelect(item);
      else value.val = item.value;
    };

    return { open, query, close, filtered, label, pick };
  },
  // content: the trigger button, the rows, an optional filter input + its
  // focus-on-open derive (kept here so it is pruned with the component).
  view: (
    { open, query, close, filtered, label, pick },
    {
      value,
      icon = null,
      header = null,
      footer = null,
      searchable = false,
      wrapClass = "",
      renderItem = null,
      ariaLabel,
    }: DropdownProps = {} as DropdownProps,
  ) => {
    const search = searchable
      ? input({
          class: "dd-search",
          placeholder: "Search...",
          oninput: (e: Event) => (query.val = (e.target as HTMLInputElement).value),
          onkeydown: (e: KeyboardEvent) => {
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
        if (open.val) setTimeout(() => search.focus(), 0);
        else (query.val = ""), (search.value = "");
      });

    const trigger = button(
      {
        class: "dd-btn",
        type: "button",
        "aria-haspopup": "true",
        "aria-expanded": () => String(open.val),
        ...(ariaLabel ? { "aria-label": ariaLabel } : {}),
        onclick: (e: Event) => {
          e.stopPropagation();
          open.val = !open.val;
        },
      },
      icon ? Icon(icon, { size: 14, strokeWidth: 2 }) : null,
      span({ class: "dd-label" }, label),
      Icon("chevron", { size: 13, class: "chev", strokeWidth: 2.4 }),
    );

    // role=button + tabindex makes the row keyboard-operable (WCAG 2.1.1); some consumers
    // (project-picker) inject their own action buttons via renderItem, so the row stays a div
    // rather than a <button> to keep those reachable.
    const Row = (item: DropdownItem) =>
      div(
        {
          class: () => "dd-item" + (value.val === item.value ? " sel" : ""),
          role: "button",
          tabindex: "0",
          "aria-current": () => (value.val === item.value ? "true" : "false"),
          onclick: () => pick(item),
          onkeydown: (e: KeyboardEvent) => {
            if (e.key === "Enter" || e.key === " ") {
              e.preventDefault();
              pick(item);
            }
          },
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
