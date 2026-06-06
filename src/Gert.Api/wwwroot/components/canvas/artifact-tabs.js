// components/canvas/artifact-tabs.js — the editor-style tab list (.ctabs).
// One tab per artifact; binds to state/artifacts (van-x) + ui.activeArtifact.
import van from "van";
import { component } from "../../lib/component.js";
import * as artifacts from "../../state/artifacts.js";
import * as ui from "../../state/ui.js";

const { div, span } = van.tags;

const TI_LABEL = { md: "M↓", html: "<>", svg: "▱", py: ".py" };

const Tab = (a) =>
  div(
    {
      class: () =>
        "ctab" +
        (ui.activeArtifact.val === a.id && !ui.showKnowledge.val ? " active" : ""),
      "data-tab": a.kind,
      onclick: () => ui.openArtifact(a.id),
    },
    span({ class: "ti " + a.kind }, TI_LABEL[a.kind] || "?"),
    a.name || "untitled",
  );

export const ArtifactTabs = component({
  name: "artifact-tabs",
  css: `
    .ctabs{display:flex; gap:3px; flex:1; min-width:0; overflow-x:auto; scrollbar-width:none;}
    .ctabs::-webkit-scrollbar{display:none;}
    .ctab{display:flex; align-items:center; gap:6px; padding:6px 9px; border-radius:7px 7px 0 0; cursor:pointer; color:var(--ink-2); font-family:var(--mono); font-size:11px; white-space:nowrap; border:1px solid transparent; border-bottom:none; transition:.13s;}
    .ctab:hover{background:var(--surface-2); color:var(--ink);}
    .ctab .ti{width:12px; height:12px; flex:none; border-radius:3px; display:grid; place-items:center; font-size:7.5px; font-weight:700; color:var(--on-accent); letter-spacing:-.02em;}
    .ti.md{background:var(--type-md);} .ti.html{background:var(--coral);} .ti.svg{background:var(--amber);} .ti.py{background:var(--green);}
    .ctab.active{background:var(--surface); color:var(--ink); border-color:var(--line); box-shadow:0 -1px 0 var(--coral) inset;}
  `,
  view: () =>
    div(
      { class: "ctabs" },
      () => div({ style: "display:contents" }, ...artifacts.artifacts.map((a) => Tab(a))),
    ),
});
