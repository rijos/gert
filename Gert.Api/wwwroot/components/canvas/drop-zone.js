// components/canvas/drop-zone.js — click/drag upload target.
// Calls services/documents.upload; never fetches directly.
import van from "van";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import * as svc from "../../services/documents.js";
import { attempt } from "../../lib/action.js";

const { div } = van.tags;

const uploadFiles = (files) =>
  [...(files || [])].forEach((f) => attempt(() => svc.upload(f), "Upload failed"));

export const DropZone = component({
  name: "drop-zone",
  css: `
    .dropzone{margin:13px 18px; border:1.5px dashed var(--line-strong); border-radius:var(--r); padding:18px 14px; text-align:center; color:var(--ink-faint); cursor:pointer; transition:.16s;}
    .dropzone:hover,.dropzone.over{border-color:var(--accent); color:var(--accent-deep); background:var(--accent-soft);}
    .dropzone svg{width:20px; height:20px; margin-bottom:6px;}
    .dropzone .dz1{font-size:12.5px; font-weight:600; color:inherit;}
    .dropzone .dz2{font-family:var(--mono); font-size:10px; margin-top:3px;}
  `,
  view: () => {
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
  },
});
