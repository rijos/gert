// components/canvas/artifact-tabs.js — the editor-style tab list (.ctabs).
// One tab per artifact; binds to state/artifacts (van-x) + ui.activeArtifact.
import van from "van";
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

export const ArtifactTabs = () =>
  div(
    { class: "ctabs" },
    () => div({ style: "display:contents" }, ...artifacts.artifacts.map((a) => Tab(a))),
  );
