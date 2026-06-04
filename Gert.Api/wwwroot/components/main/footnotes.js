// components/main/footnotes.js — footnote list under a bot message.
// Binds to a van-x reactive citations list on the message.
import van from "van";

const { div, span } = van.tags;

export const Footnotes = (citations) =>
  () =>
    citations.length
      ? div(
          { class: "footnotes" },
          ...citations.map((c) =>
            div(
              { class: "fn" },
              span({ class: "n" }, String(c.ordinal)),
              span(span({ class: "src" }, c.label || "")),
            ),
          ),
        )
      : div();
