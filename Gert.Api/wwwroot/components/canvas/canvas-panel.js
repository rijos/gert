// components/canvas/canvas-panel.js — right pane container. Holds the tab bar,
// the artifact stage (one Artifact per tab), and the knowledge view. Drawer
// behaviour comes from the .app state classes (app-shell.js).
import van from "van";
import { CanvasBar } from "./canvas-bar.js";
import { Artifact } from "./artifact.js";
import { KnowledgePanel } from "./knowledge-panel.js";
import * as artifacts from "../../state/artifacts.js";
import * as ui from "../../state/ui.js";

const { aside, div } = van.tags;

export const CanvasPanel = () =>
  aside(
    { class: "panel" },
    CanvasBar(),
    div(
      { class: "canvas-stage" },
      // one viewer per artifact; only the active (non-KB) one shows.
      () =>
        div(
          { style: "display:contents" },
          ...artifacts.artifacts.map((a) =>
            Artifact({
              artifact: a,
              active: () =>
                ui.activeArtifact.val === a.id && !ui.showKnowledge.val,
            }),
          ),
        ),
      // empty hint when no artifacts and KB not shown
      () =>
        !artifacts.artifacts.length && !ui.showKnowledge.val
          ? div(
              { class: "canvas-empty" },
              "Artifacts Gert produces in this thread appear here.",
            )
          : div(),
      KnowledgePanel(),
    ),
  );
