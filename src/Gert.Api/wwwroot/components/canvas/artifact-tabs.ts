// The editor-style tab list (.ctabs), one tab per artifact. Long conversations overflow
// the strip, so three affordances keep every file reachable: the strip scrolls (mouse wheel
// mapped to horizontal), the active tab auto-scrolls into view, and a pinned "all files"
// dropdown lists the lot.
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import type { Artifact } from "../../state/artifacts.js";
import { Icon } from "../../icons/icons.js";
import { Menu } from "../ui/menu.js";
import * as artifacts from "../../state/artifacts.js";
import * as ui from "../../state/ui.js";
import { t } from "../../lib/i18n.js";

const { div, span, button } = van.tags;

const TI_LABEL: Record<string, string> = { md: "md", html: "<>", svg: "svg", py: ".py" };

const TypeIcon = (a: Artifact) => span({ class: "ti " + a.kind }, TI_LABEL[a.kind] || "?");

const Tab = (a: Artifact) =>
  button(
    {
      class: () => "ctab" + (ui.activeArtifact.val === a.id && !ui.showKnowledge.val ? " active" : ""),
      type: "button",
      role: "tab",
      "data-tab": a.kind,
      "aria-selected": () => String(ui.activeArtifact.val === a.id && !ui.showKnowledge.val),
      onclick: () => ui.openArtifact(a.id, true),
    },
    TypeIcon(a),
    a.name || "untitled",
  );

export const ArtifactTabs = component({
  name: "artifact-tabs",
  css: `
    .ctabs-wrap {
      display: flex;
      align-items: center;
      gap: 4px;
      flex: 1;
      min-width: 0;
      position: relative;
    }

    .ctabs {
      display: flex;
      gap: 3px;
      flex: 1;
      min-width: 0;
      overflow-x: auto;
      scrollbar-width: none;
    }

    .ctabs::-webkit-scrollbar {
      display: none;
    }

    .ctab {
      display: flex;
      align-items: center;
      gap: 6px;
      padding: 6px 9px;
      border-radius: 7px 7px 0 0;
      cursor: pointer;
      background: none;
      color: var(--ink-2);
      font-family: var(--mono);
      font-size: var(--fs-xs);
      white-space: nowrap;
      border: 1px solid transparent;
      border-bottom: none;
      transition: var(--t-fast);
    }

    .ctab:hover {
      background: var(--surface-2);
      color: var(--ink);
    }

    .ctab .ti,
    .ca-item .ti {
      min-width: 13px;
      height: 13px;
      padding: 0 2px;
      flex: none;
      border-radius: 3px;
      display: grid;
      place-items: center;
      font-size: 7.5px;
      font-weight: 700;
      color: var(--on-chip);
      letter-spacing: -.02em;
    }

    .ti.md {
      background: var(--type-md);
    }
    .ti.html {
      background: var(--coral);
    }
    .ti.svg {
      background: var(--amber);
    }
    .ti.py {
      background: var(--type-py);
    }

    .ctab.active {
      background: var(--surface);
      color: var(--ink);
      border-color: var(--line);
      box-shadow: 0 -1px 0 var(--coral) inset;
    }

    /* the pinned "all files" trigger - never scrolls out of reach */
    .ctab-all {
      display: flex;
      align-items: center;
      gap: 4px;
      flex: none;
      padding: 5px 7px;
      background: none;
      border: 1px solid var(--line);
      border-radius: 7px;
      color: var(--ink-2);
      font-family: var(--mono);
      font-size: var(--fs-2xs);
      cursor: pointer;
      transition: var(--t-fast);
    }

    .ctab-all:hover {
      border-color: var(--coral);
      color: var(--coral-deep);
    }

    .ctab-all .chev {
      transition: var(--t-slow) var(--ease);
    }

    .ctabs-wrap.open .chev {
      transform: rotate(180deg);
    }

    .ctabs-wrap .menu {
      width: 232px;
      max-height: 50vh;
      overflow-y: auto;
    }

    .ctabs-wrap.open .menu {
      opacity: 1;
      visibility: visible;
      transform: none;
      pointer-events: auto;
    }

    .ca-item {
      display: flex;
      align-items: center;
      gap: 7px;
      padding: var(--sp-2) var(--sp-3);
      border-radius: var(--r-sm);
      cursor: pointer;
      transition: var(--t-fast);
      font-family: var(--mono);
      font-size: var(--fs-xs);
      color: var(--ink-2);
    }

    .ca-item:hover {
      background: var(--surface-2);
      color: var(--ink);
    }

    .ca-item.sel {
      background: var(--coral-soft);
      color: var(--coral-deep);
    }

    .ca-item .ca-name {
      flex: 1;
      min-width: 0;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
  `,
  // logic: the "all files" dropdown open state. The scroll strip, trigger, and
  // the auto-scroll derive are DOM-scoped, so they stay in `view`.
  setup: () => {
    const open = van.state(false);
    return { open };
  },
  view: ({ open }) => {
    const strip = div(
      {
        class: "ctabs",
        role: "tablist",
        "aria-label": "Open files",
        // a mouse wheel only has a vertical axis - map it onto the strip
        onwheel: (e: WheelEvent) => {
          if (!e.deltaY || e.deltaX) return;
          e.preventDefault();
          (e.currentTarget as HTMLElement).scrollLeft += e.deltaY;
        },
      },
      () => div({ style: "display:contents" }, ...artifacts.artifacts.map((a) => Tab(a))),
    );

    const trigger = button(
      {
        class: "ctab-all",
        type: "button",
        title: t("All files"),
        "aria-label": "List all files",
        onclick: (e: Event) => {
          e.stopPropagation();
          open.val = !open.val;
        },
      },
      () => String(artifacts.artifacts.length),
      Icon("chevron", { size: 11, class: "chev", strokeWidth: 2.4 }),
    );

    const wrap = Menu({
      wrapClass: "ctabs-wrap",
      open,
      trigger: [
        strip,
        // the list earns its place only once tabs can plausibly overflow
        () => (artifacts.artifacts.length > 1 ? trigger : span()),
      ],
      children: [
        div({ class: "menu-h" }, t("Files in this conversation")),
        () =>
          div(
            ...artifacts.artifacts.map((a) =>
              div(
                {
                  class: () =>
                    "ca-item" +
                    (ui.activeArtifact.val === a.id && !ui.showKnowledge.val ? " sel" : ""),
                  role: "button",
                  tabindex: "0",
                  onclick: () => {
                    open.val = false;
                    ui.openArtifact(a.id, true);
                  },
                  onkeydown: (e: KeyboardEvent) => {
                    if (e.key === "Enter" || e.key === " ") {
                      e.preventDefault();
                      open.val = false;
                      ui.openArtifact(a.id, true);
                    }
                  },
                },
                TypeIcon(a),
                span({ class: "ca-name" }, a.name || "untitled"),
              ),
            ),
          ),
      ],
    });

    // keep the newly-active tab visible - streams append tabs off-screen right
    // van.d.ts copies vanjs-core's derive as 1-arg, but the runtime takes the
    // (init, dom) scope args this codebase relies on; cast names that real shape.
    (van.derive as (f: () => unknown, s: undefined, dom: Node) => unknown)(
      () => {
        const id = ui.activeArtifact.val;
        if (id == null) return;
        queueMicrotask(() =>
          strip.querySelector(".ctab.active")?.scrollIntoView({ block: "nearest", inline: "nearest" }),
        );
      },
      undefined,
      wrap,
    );

    return wrap;
  },
});
