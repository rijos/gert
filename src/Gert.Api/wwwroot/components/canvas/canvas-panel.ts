// components/canvas/canvas-panel.js - right pane container. Holds the tab bar,
// the artifact stage (one Artifact per tab), and the knowledge view. Drawer
// behaviour comes from the .app state classes (app-shell.js).
import van from "/lib/van.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { CanvasBar } from "./canvas-bar.js";
import { Artifact } from "./artifact.js";
import { KnowledgePanel } from "./knowledge-panel.js";
import * as artifacts from "../../state/artifacts.js";
import * as ui from "../../state/ui.js";

const { aside, div } = van.tags;

// Drag the panel's left edge to resize. The panel hugs the viewport's right
// edge, so its width is (viewport width - cursor x). Width persists via ui.
const startResize = (e: PointerEvent) => {
  e.preventDefault();
  const app = document.querySelector(".app");
  app?.classList.add("resizing");
  const onMove = (ev: PointerEvent) => ui.setPanelWidth(window.innerWidth - ev.clientX);
  const onUp = () => {
    app?.classList.remove("resizing");
    window.removeEventListener("pointermove", onMove);
    window.removeEventListener("pointerup", onUp);
  };
  window.addEventListener("pointermove", onMove);
  window.addEventListener("pointerup", onUp);
};

export const CanvasPanel = component({
  name: "canvas-panel",
  css: `
    /* paper grain rides the pane background (tokens.css --grain-img) */
    .panel {
      background: var(--canvas-bg);
      background-image: var(--grain-img);
      background-size: 18px 18px;
      border-left: 1px solid var(--line);
      display: flex;
      flex-direction: column;
      overflow: hidden;
      min-width: 0;
      position: relative;
    }

    .canvas-stage {
      flex: 1;
      min-height: 0;
      overflow: hidden;
      display: flex;
      flex-direction: column;
    }

    /* the stage's two pane kinds (artifact viewer / knowledge view) show one at a time */
    .art-doc,
    .kb-view {
      display: none;
      flex: 1;
      min-height: 0;
      flex-direction: column;
    }

    .art-doc.active,
    .kb-view.active {
      display: flex;
    }

    .canvas-empty {
      flex: 1;
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      gap: 10px;
      color: var(--ink-3);
      font-family: var(--mono);
      font-size: var(--fs-xs);
      padding: 24px;
      text-align: center;
      max-width: 280px;
      margin: 0 auto;
      line-height: var(--lh-ui);
    }

    .canvas-empty svg {
      width: 22px;
      height: 22px;
      opacity: .7;
    }
  `,
  view: () =>
  aside(
    { class: "panel", "aria-label": "Canvas" },
    // Pointer-only resize affordance: hidden from AT (the panel content is fully reachable
    // without it, and there is no content loss if it is never used).
    div({ class: "resize-handle", title: "Drag to resize", "aria-hidden": "true", onpointerdown: startResize }),
    CanvasBar(),
    div(
      { class: "canvas-stage" },
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
      () =>
        !artifacts.artifacts.length && !ui.showKnowledge.val
          ? div(
              { class: "canvas-empty" },
              Icon("file", { size: 22, strokeWidth: 1.6 }),
              "Artifacts Gert produces in this thread appear here.",
            )
          : div(),
      KnowledgePanel(),
    ),
  ),
});
