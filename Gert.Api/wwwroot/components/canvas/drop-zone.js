// components/canvas/drop-zone.js — click/drag upload target.
// Calls services/documents.upload; never fetches directly.
import van from "van";
import { Icon } from "../../icons/icons.js";
import * as svc from "../../services/documents.js";
import { attempt } from "../../lib/action.js";

const { div } = van.tags;

const uploadFiles = (files) =>
  [...(files || [])].forEach((f) => attempt(() => svc.upload(f), "Upload failed"));

export const DropZone = () => {
  const over = van.state(false);
  const fileInput = van.tags.input({
    type: "file",
    multiple: true,
    style: "display:none",
    onchange: (e) => {
      uploadFiles(e.target.files);
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
        uploadFiles(e.dataTransfer?.files);
      },
    },
    Icon("upload", { size: 20, strokeWidth: 1.9 }),
    div({ class: "dz1" }, "Drop files or click to upload"),
    div({ class: "dz2" }, "pdf · docx · md · txt"),
    fileInput,
  );
};
