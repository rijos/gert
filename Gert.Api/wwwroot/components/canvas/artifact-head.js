// components/canvas/artifact-head.js — type badge + name + Rendered/Source
// seg toggle (shared across viewers). `mode` is a van.state owned by artifact.js.
import van from "van";
import { SegToggle } from "../ui/seg-toggle.js";

const { div, span } = van.tags;

const TYPE_LABEL = { md: "MD", html: "HTML", svg: "SVG", py: "PY" };

// renderLabel/sourceLabel let html/svg say "Preview" instead of "Rendered".
export const ArtifactHead = ({ artifact, mode, renderLabel = "Rendered" } = {}) =>
  div(
    { class: "art-head" },
    span({ class: "atype " + artifact.kind }, TYPE_LABEL[artifact.kind] || "?"),
    // filename is a van text node — XSS-safe; CSS adds unicode-bidi:isolate.
    span({ class: "aname" }, artifact.name || "untitled"),
    artifact.kind === "py"
      ? span(
          { class: "gen" },
          span({ class: "gd" }),
          (artifact.problems?.length || 0) + " problems",
        )
      : SegToggle({
          options: [
            { value: "render", label: renderLabel },
            { value: "source", label: "Source" },
          ],
          value: () => mode.val,
          onSelect: (v) => (mode.val = v),
        }),
  );
