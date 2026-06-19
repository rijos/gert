// components/canvas/artifacts/md-math.js - the math leaf of the markdown
// renderer. The structural renderer calls MdMath for every math_block /
// math_inline node and inserts the returned DOM verbatim.
//
// Invariants worth keeping: MdMath wraps smath's renderMath, whose output shape
// (span.md-math[.md-math-display] wrapping native <math> MathML) is IDENTICAL to
// the inline tree the renderer emitted before, so every selector, the byte-oracle
// and the .md-math* CSS still hold. smath's closed MML allow-list + per-formula
// try/catch -> literal TeX degrade bad math PER-FORMULA, never document-wide
// (HARD CONTRACT 3). view() returns renderMath's span directly (built via
// createElement/createElementNS, never van.tags/innerHTML), so F4 holds.
import { component } from "../../../lib/component.js";
import { renderMath } from "../../../lib/smath.js";

export const MdMath = component({
  name: "md-math",
  css: `
    /* Math wrappers (smath -> native <math> MathML). Math lands wherever
       renderMarkdown runs (chat body, md artifact). Inline math rides the text
       flow; display math is its own block. Color inherits (MathML paints with
       currentColor), so math follows the theme like the text around it. The
       .md-math-block wrapper (the scrolling display container the structural
       renderer puts around a math_block) stays in styles/base.css - it is the
       renderer's node, not this leaf's. */
    .md-math-display {
      display: block;
    }

    /* the native <math> element smath emits: a math font stack + colour/display
       essentials (smath builds standard MathML Core - no impl-specific helpers).
       Same-origin, so style-src 'self' holds. */
    math {
      font-family: "Cambria Math", "STIX Two Math", STIXTwoMath-Regular,
        "Noto Sans Math", NotoSansMath-Regular, math;
      font-style: normal;
      font-weight: normal;
      line-height: normal;
      direction: ltr;
      /* keep Firefox from dropping the dot on i / j under math styling */
      font-feature-settings: "dtls" off;
    }

    /* fractions, radicals and bars paint with the surrounding text colour, so
       math follows the theme (MathML uses currentColor). */
    math * {
      border-color: currentColor;
    }

    /* Display math is a centred block. Chromium honours the display="block"
       attribute smath sets; Firefox/WebKit need the explicit CSS. */
    math[display="block"] {
      display: block;
      width: 100%;
    }
  `,
  // latex/display come from the structural renderer's math node (lib/render/dom.js);
  // typed loosely (unknown) because renderMath stringifies/coerces them itself.
  view: ({ latex, display }: { latex?: unknown; display?: unknown }) =>
    renderMath(latex, { display: !!display }),
});

export default MdMath;
