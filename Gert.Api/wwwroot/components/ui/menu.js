// components/ui/menu.js — dropdown menu shell (.menu) with open/close state.
// Returns a wrapper whose `class` reflects `open`; clicking outside closes it.
import van from "van";

const { div } = van.tags;

// trigger: node (the button). open: van.state(boolean). header optional.
// children: menu rows.
export const Menu = ({ trigger, open, wrapClass = "model-picker", children = [] } = {}) => {
  const root = div(
    { class: () => wrapClass + (open.val ? " open" : "") },
    trigger,
    div(
      {
        class: "menu",
        onclick: (e) => e.stopPropagation(),
      },
      ...children,
    ),
  );

  // close on outside click (registered once per instance)
  const onDoc = () => (open.val = false);
  document.addEventListener("click", onDoc);
  return root;
};
