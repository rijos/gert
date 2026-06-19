// components/search-overlay.js - full-screen search over chats or projects.
// Imperative open like ui/modal.js (transient, not a route). Debounced query ->
// GET with q/limit/offset; scrolling near the bottom loads the next page
// (infinite scroll - a page shorter than PAGE means the end).
import van from "/lib/van.js";
import type { State } from "/lib/van.js";
import { component } from "../lib/component.js";
import { Icon } from "../icons/icons.js";
import { fmtRelative } from "../lib/format.js";
import * as http from "../services/http.js";
import type { WireConversation, WireProject } from "../services/wire.js";
import * as chat from "../state/chat.js";
import * as projectsSvc from "../services/projects.js";
import { navigate } from "../lib/router.js";
import { attempt } from "../lib/action.js";
import { t } from "../lib/i18n.js";

const { div, input, span, button, h2 } = van.tags;

const PAGE = 30;

// One search result row off the wire: the chat-list and project-list endpoints
// return overlapping but distinct shapes, so the fields each mode reads are
// optional here. `id` is always present (the open() handlers route by it).
interface SearchItem {
  id: string;
  title?: string;
  name?: string;
  updated_at?: string;
  conversation_count?: number;
}

// A search mode: a titled, debounced query against one endpoint, plus the
// row-render and open-on-click behaviours that go with that result shape.
interface SearchMode {
  title: string;
  placeholder: string;
  fetch: (q: string, offset: number) => Promise<SearchItem[]>;
  row: (item: SearchItem) => { icon: string; label: string; meta: string };
  open: (item: SearchItem, close: () => void) => void;
}

const MODES: Record<string, SearchMode> = {
  chats: {
    title: t("Search chats"),
    placeholder: t("Search your chats..."),
    fetch: (q, offset) =>
      // The conversations endpoint returns WireConversation rows - the SearchItem view subset.
      http.get<WireConversation[]>(
        `/projects/${chat.activeProjectId.val}/conversations` +
          `?q=${encodeURIComponent(q)}&limit=${PAGE}&offset=${offset}`,
      ),
    row: (item) => ({
      icon: "edit",
      label: item.title || t("Untitled"),
      meta: fmtRelative(item.updated_at ?? ""),
    }),
    open: (item, close) => {
      close();
      navigate("/c/" + item.id);
    },
  },
  projects: {
    title: t("Search projects"),
    placeholder: t("Search your projects..."),
    fetch: (q, offset) =>
      // The projects endpoint returns WireProject rows - the SearchItem view subset.
      http.get<WireProject[]>(`/projects?q=${encodeURIComponent(q)}&limit=${PAGE}&offset=${offset}`),
    row: (item) => ({
      icon: "book",
      label: item.name ?? "",
      meta: `${item.conversation_count ?? 0} chats - ${fmtRelative(item.updated_at ?? "")}`,
    }),
    open: (item, close) => {
      close();
      attempt(async () => {
        const recent = await projectsSvc.select(item.id);
        navigate(recent ? "/c/" + recent.id : "/");
      }, "Couldn't switch project");
    },
  },
};

// openSearch is the sole caller and always supplies both fields; the `= {}`
// default only guards an argless call, and the cast keeps `close` non-optional
// for the value uses below without weakening that guard (runtime default is {}).
type SearchArgs = { mode?: string; close: () => void };

// setup's typed bag; `items` is a snapshot list re-bound wholesale per page.
interface SearchState {
  m: SearchMode;
  items: State<SearchItem[]>;
  querying: State<boolean>;
  onInput: (e: Event) => void;
  onScroll: (e: Event) => void;
}

export const SearchOverlay = component({
  name: "search-overlay",
  css: `
    .so-scrim {
      position: fixed;
      inset: 0;
      background: var(--scrim);
      backdrop-filter: blur(2px);
      z-index: 70;
      display: flex;
      flex-direction: column;
      align-items: center;
      padding: 8vh 16px 6vh;
    }

    .so {
      width: min(640px, 100%);
      max-height: 100%;
      display: flex;
      flex-direction: column;
      background: var(--surface);
      border: 1px solid var(--line);
      border-radius: var(--r);
      box-shadow: var(--shadow-modal);
      animation: rise var(--t-slow) var(--ease) backwards;
      overflow: hidden;
    }

    .so-head {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 14px 16px;
      border-bottom: 1px solid var(--line);
      flex: none;
    }

    .so-head h2 {
      font-family: var(--display);
      font-size: var(--fs-lg);
      font-weight: 600;
      flex: none;
    }

    .so-input {
      flex: 1;
      font-family: var(--sans);
      font-size: var(--fs-md);
      color: var(--ink);
      background: var(--surface-2);
      border: 1px solid var(--line);
      border-radius: var(--r-sm);
      padding: 9px 12px;
      outline: none;
    }

    .so-input:focus {
      border-color: var(--coral);
    }

    .so-list {
      flex: 1;
      min-height: 120px;
      overflow-y: auto;
      padding: 8px;
    }

    .so-row {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 10px 12px;
      border-radius: var(--r-sm);
      cursor: pointer;
      transition: var(--t-fast);
    }

    .so-row:hover {
      background: var(--coral-soft);
    }

    .so-row svg {
      flex: none;
      color: var(--ink-3);
    }

    .so-row .so-label {
      flex: 1;
      min-width: 0;
      font-size: var(--fs-md);
      font-weight: 500;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }

    .so-row .so-meta {
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      color: var(--ink-3);
      flex: none;
    }

    .so-state {
      padding: 18px 12px;
      text-align: center;
      font-size: var(--fs-sm);
      color: var(--ink-3);
    }
  `,
  // logic: resolve the mode, hold the result list + querying flag, and own the
  // debounced query + infinite-scroll handlers (newest request wins via ticket).
  // The initial page loads here (setup runs once, before view).
  setup: ({ mode = "chats" }: SearchArgs = {} as SearchArgs): SearchState => {
    // mode is always one of the MODES keys (openSearch passes "chats"|"projects",
    // defaulting to "chats"); the `!` reflects that invariant (string index else
    // widens to | undefined).
    const m = MODES[mode]!;
    const items = van.state<SearchItem[]>([]); // replaced wholesale per page append (snapshot render)
    const exhausted = van.state(false);
    const querying = van.state(false);
    let q = "";
    let offset = 0;
    let ticket = 0; // newest request wins; stale pages are dropped

    const load = (reset: boolean) =>
      attempt(async () => {
        const my = ++ticket;
        if (reset) {
          offset = 0;
          exhausted.val = false;
        }
        querying.val = true;
        const page = (await m.fetch(q, offset)) || [];
        if (my !== ticket) return; // a newer query/page superseded this one
        querying.val = false;
        if (page.length < PAGE) exhausted.val = true;
        offset += page.length;
        items.val = reset ? page : [...items.val, ...page];
      }, "Search failed");

    let debounce = 0;
    const onInput = (e: Event) => {
      q = (e.target as HTMLInputElement).value.trim();
      clearTimeout(debounce);
      debounce = setTimeout(() => load(true), 250);
    };

    // infinite scroll: within 120px of the bottom and more to fetch -> next page
    const onScroll = (e: Event) => {
      const el = e.target as HTMLElement;
      if (exhausted.val || querying.val) return;
      if (el.scrollHeight - el.scrollTop - el.clientHeight < 120) load(false);
    };

    load(true); // initial page: everything, newest first

    return { m, items, querying, onInput, onScroll };
  },
  // content: the head (title + autofocus input + close) and the result list.
  // close arrives as a call arg; the `() => ...` list body is the DOM-scoped
  // reactive binding (items/querying), so it stays here in view.
  view: (
    { m, items, querying, onInput, onScroll }: SearchState,
    { close }: SearchArgs = {} as SearchArgs,
  ) => {
    return div({ class: "so", role: "dialog", "aria-modal": "true", "aria-labelledby": "search-overlay-title" },
      div({ class: "so-head" },
        h2({ id: "search-overlay-title" }, m.title),
        input({
          class: "so-input",
          placeholder: m.placeholder,
          "aria-label": m.title,
          autofocus: true,
          oninput: onInput,
          onkeydown: (e: KeyboardEvent) => {
            if (e.key === "Escape") close();
          },
        }),
        button({ class: "ghost", title: t("Close"), "aria-label": t("Close"), onclick: close },
          Icon("close", { size: 15, strokeWidth: 2.2 }),
        ),
      ),
      // role=status so result-state changes (Searching / Nothing found / Loading) are announced.
      div({ class: "so-list", onscroll: onScroll },
        () => {
          const list = items.val;
          if (!list.length) {
            return div({ class: "so-state", role: "status", "aria-live": "polite" },
              querying.val ? t("Searching...") : t("Nothing found."),
            );
          }
          return div(
            ...list.map((item) => {
              const r = m.row(item);
              return div(
                {
                  class: "so-row",
                  role: "button",
                  tabindex: "0",
                  onclick: () => m.open(item, close),
                  onkeydown: (e: KeyboardEvent) => {
                    if (e.key === "Enter" || e.key === " ") {
                      e.preventDefault();
                      m.open(item, close);
                    }
                  },
                },
                Icon(r.icon, { size: 14, strokeWidth: 2 }),
                span({ class: "so-label" }, r.label),
                span({ class: "so-meta" }, r.meta),
              );
            }),
            () =>
              querying.val
                ? div({ class: "so-state", role: "status", "aria-live": "polite" }, t("Loading..."))
                : span(),
          );
        },
      ),
    );
  },
});

// Open the overlay over the page; returns close(). Esc / scrim click close. Focus moves into
// the dialog, is trapped within it, and returns to the opener on close (WCAG 2.4.3 / 2.1.2).
export const openSearch = (mode: string) => {
  const opener = document.activeElement as HTMLElement | null;
  const tabbables = () =>
    [
      ...host.querySelectorAll<HTMLElement>(
        'a[href],button:not([disabled]),input:not([disabled]),[tabindex]:not([tabindex="-1"])',
      ),
    ].filter((el) => el.offsetParent !== null);
  const onKey = (e: KeyboardEvent) => {
    if (e.key === "Escape") {
      close();
      return;
    }
    if (e.key === "Tab") {
      const f = tabbables();
      if (!f.length) return;
      const first = f[0]!;
      const last = f[f.length - 1]!;
      const active = document.activeElement;
      if (e.shiftKey && (active === first || !host.contains(active))) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && (active === last || !host.contains(active))) {
        e.preventDefault();
        first.focus();
      }
    }
  };
  const close = () => {
    host.remove();
    document.removeEventListener("keydown", onKey);
    opener?.focus?.();
  };
  const host = div(
    {
      class: "so-scrim",
      onclick: (e: Event) => {
        if (e.target === host) close();
      },
    },
    SearchOverlay({ mode, close }),
  );
  van.add(document.body, host);
  document.addEventListener("keydown", onKey);
  (host.querySelector(".so-input") as HTMLElement | null)?.focus();
  return close;
};
