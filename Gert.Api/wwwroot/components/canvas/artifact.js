// components/canvas/artifact.js — polymorphic dispatcher: picks a viewer by
// artifact.kind, wraps it with the shared header + Rendered/Source mode state.
import van from "van";
import { ArtifactHead } from "./artifact-head.js";
import { MarkdownArtifact } from "./artifacts/markdown-artifact.js";
import { HtmlArtifact } from "./artifacts/html-artifact.js";
import { SvgArtifact } from "./artifacts/svg-artifact.js";
import { CodeArtifact } from "./artifacts/code-artifact.js";

const { section } = van.tags;

const VIEWERS = {
  md: MarkdownArtifact,
  html: HtmlArtifact,
  svg: SvgArtifact,
  py: CodeArtifact,
};

// active: () => boolean — whether this artifact's tab is selected.
export const Artifact = ({ artifact, active } = {}) => {
  const mode = van.state("render");
  const Viewer = VIEWERS[artifact.kind] || CodeArtifact;
  const renderLabel = artifact.kind === "html" || artifact.kind === "svg"
    ? "Preview"
    : "Rendered";

  return section(
    {
      class: () => "art-doc" + (active() ? " active" : ""),
      "data-type": artifact.kind,
      "data-mode": () => mode.val,
    },
    artifact.kind === "py"
      ? ArtifactHead({ artifact, mode })
      : ArtifactHead({ artifact, mode, renderLabel }),
    Viewer({ artifact, mode }),
  );
};
