// components/search-overlay.js - full-screen search over chats or projects.
// Imperative open like ui/modal.js (transient, not a route). Debounced query ->
// GET with q/limit/offset; scrolling near the bottom loads the next page
// (infinite scroll - a page shorter than PAGE means the end).
import van from "van";
import { component } from "../lib/component.js";
import { Icon } from "../icons/icons.js";
import { fmtRelative } from "../lib/format.js";
import * as http from "../services/http.js";
import * as chat from "../state/chat.js";
import * as projectsSvc from "../services/projects.js";
import { navigate } from "../lib/router.js";
import { attempt } from "../lib/action.js";
import { t } from "../lib/i18n.js";

const { div, input, span, button, h2 } = van.tags;

const PAGE = 30;

const MODES = {
  chats: {
    title: t("Search chats"),
    placeholder: t("Search your chats..."),
    fetch: (q, offset) =>
      http.get(
        `/projects/${chat.activeProjectId.val}/conversations` +
          `?q=${encodeURIComponent(q)}&limit=${PAGE}&offset=${offset}`,
      ),
    row: (item) => ({
      icon: "edit",
      label: item.title || t("Untitled"),
      meta: fmtRelative(item.updated_at),
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
      http.get(`/projects?q=${encodeURIComponent(q)}&limit=${PAGE}&offset=${offset}`),
    row: (item) => ({
      icon: "book",
      label: item.name,
      meta: `${item.conversation_count ?? 0} chats - ${fmtRelative(item.updated_at)}`,
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

export const SearchOverlay = component({
  name: "search-overlay",
  css: `
    .so-scrim{position:fixed; inset:0; background:var(--scrim); backdrop-filter:blur(2px); z-index:70; display:flex; flex-direction:column; align-items:center; padding:8vh 16px 6vh;}
    .so{width:min(640px,100%); max-height:100%; display:flex; flex-direction:column; background:var(--surface); border:1px solid var(--line); border-radius:var(--r); box-shadow:var(--shadow-modal); animation:rise var(--t-slow) var(--ease) backwards; overflow:hidden;}
    .so-head{display:flex; align-items:center; gap:10px; padding:14px 16px; border-bottom:1px solid var(--line); flex:none;}
    .so-head h2{font-family:var(--display); font-size:var(--fs-lg); font-weight:600; flex:none;}
    .so-input{flex:1; font-family:var(--sans); font-size:var(--fs-md); color:var(--ink); background:var(--surface-2); border:1px solid var(--line); border-radius:var(--r-sm); padding:9px 12px; outline:none;}
    .so-input:focus{border-color:var(--coral);}
    .so-list{flex:1; min-height:120px; overflow-y:auto; padding:8px;}
    .so-row{display:flex; align-items:center; gap:10px; padding:10px 12px; border-radius:var(--r-sm); cursor:pointer; transition:var(--t-fast);}
    .so-row:hover{background:var(--coral-soft);}
    .so-row svg{flex:none; color:var(--ink-3);}
    .so-row .so-label{flex:1; min-width:0; font-size:var(--fs-md); font-weight:500; white-space:nowrap; overflow:hidden; text-overflow:ellipsis;}
    .so-row .so-meta{font-family:var(--mono); font-size:var(--fs-2xs); color:var(--ink-3); flex:none;}
    .so-state{padding:18px 12px; text-align:center; font-size:var(--fs-sm); color:var(--ink-3);}
  `,
  view: ({ mode = "chats", close } = {}) => {
    // -- logic -----------------------------------
    const m = MODES[mode];
    const items = van.state([]); // replaced wholesale per page append (snapshot render)
    const exhausted = van.state(false);
    const querying = van.state(false);
    let q = "";
    let offset = 0;
    let ticket = 0; // newest request wins; stale pages are dropped

    const load = (reset) =>
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
    const onInput = (e) => {
      q = e.target.value.trim();
      clearTimeout(debounce);
      debounce = setTimeout(() => load(true), 250);
    };

    // infinite scroll: within 120px of the bottom and more to fetch -> next page
    const onScroll = (e) => {
      const el = e.target;
      if (exhausted.val || querying.val) return;
      if (el.scrollHeight - el.scrollTop - el.clientHeight < 120) load(false);
    };

    load(true); // initial page: everything, newest first

    // -- content ---------------------------------
    const searchInput = input({
      class: "so-input",
      placeholder: m.placeholder,
      autofocus: true,
      oninput: onInput,
      onkeydown: (e) => {
        if (e.key === "Escape") close();
      },
    });

    return div(
      { class: "so" },
      div(
        { class: "so-head" },
        h2(m.title),
        searchInput,
        button(
          { class: "ghost", title: t("Close"), onclick: close },
          Icon("close", { size: 15, strokeWidth: 2.2 }),
        ),
      ),
      div(
        { class: "so-list", onscroll: onScroll },
        () => {
          const list = items.val;
          if (!list.length) {
            return div(
              { class: "so-state" },
              querying.val ? t("Searching...") : t("Nothing found."),
            );
          }
          return div(
            ...list.map((item) => {
              const r = m.row(item);
              return div(
                { class: "so-row", onclick: () => m.open(item, close) },
                Icon(r.icon, { size: 14, strokeWidth: 2 }),
                span({ class: "so-label" }, r.label),
                span({ class: "so-meta" }, r.meta),
              );
            }),
            () =>
              querying.val
                ? div({ class: "so-state" }, t("Loading..."))
                : span(),
          );
        },
      ),
    );
  },
});

// Open the overlay over the page; returns close(). Esc / scrim click close.
export const openSearch = (mode) => {
  const onKey = (e) => {
    if (e.key === "Escape") close();
  };
  const close = () => {
    host.remove();
    document.removeEventListener("keydown", onKey);
  };
  const host = div(
    {
      class: "so-scrim",
      onclick: (e) => {
        if (e.target === host) close();
      },
    },
    SearchOverlay({ mode, close }),
  );
  van.add(document.body, host);
  document.addEventListener("keydown", onKey);
  return close;
};
