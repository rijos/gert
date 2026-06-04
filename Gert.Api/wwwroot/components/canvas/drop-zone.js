// components/canvas/drop-zone.js — click/drag upload target.
// Calls services/documents.upload; never fetches directly.
import van from "van";
import { Icon } from "../../icons/icons.js";
import * as svc from "../../services/documents.js";

const { div } = van.tags;

export const DropZone = () => {
  const over = van.state(false);
  const fileInput = van.tags.input({
    type: "file",
    multiple: true,
    style: "display:none",
    onchange: (e) => {
      [...(e.target.files || [])].forEach((f) => svc.upload(f).catch(() => {}));
      e.target.value = "";
    },
  });

  return div(
    {
      class: () => "dropzone" + (over.val ? " over" : ""),
      onclick: () => fileInput.click(),
      ondragover: (e) => {
        e.preventDefault();
        over.val = true;
      },
      ondragleave: () => (over.val = false),
      ondrop: (e) => {
        e.preventDefault();
        over.val = false;
        [...(e.dataTransfer?.files || [])].forEach((f) =>
          svc.upload(f).catch(() => {}),
        );
      },
    },
    Icon("upload", { size: 20, strokeWidth: 1.9 }),
    div({ class: "dz1" }, "Drop files or click to upload"),
    div({ class: "dz2" }, "pdf · docx · md · txt"),
    fileInput,
  );
};
