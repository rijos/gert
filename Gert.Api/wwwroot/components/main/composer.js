// components/main/composer.js — autogrow textarea + attach/use-docs toggles +
// send + hint. Calls services/chat.send; never fetches directly.
import van from "van";
import { Icon } from "../../icons/icons.js";
import * as chatSvc from "../../services/chat.js";
import * as chat from "../../state/chat.js";
import * as knowledge from "../../state/knowledge.js";
import * as docsSvc from "../../services/documents.js";

const { div, textarea, button } = van.tags;

const autogrow = (t) => {
  t.style.height = "auto";
  t.style.height = Math.min(t.scrollHeight, 160) + "px";
};

export const Composer = () => {
  const ta = textarea({
    rows: 1,
    placeholder: "Message Gert…  ⌘↵ to send",
    oninput: (e) => autogrow(e.target),
    onkeydown: (e) => {
      if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        submit();
      }
    },
  });

  const submit = () => {
    const text = ta.value;
    if (!text.trim() || chat.streaming.val) return;
    chatSvc.send(text);
    ta.value = "";
    autogrow(ta);
  };

  const fileInput = van.tags.input({
    type: "file",
    style: "display:none",
    onchange: (e) => {
      const f = e.target.files?.[0];
      if (f) docsSvc.upload(f).catch(() => {});
      e.target.value = "";
    },
  });

  return div(
    { class: "composer-wrap" },
    div(
      { class: "composer" },
      ta,
      div(
        { class: "crow" },
        button(
          { class: "cbtn", onclick: () => fileInput.click() },
          Icon("attach", { size: 14, strokeWidth: 2 }),
          "Attach",
        ),
        fileInput,
        button(
          {
            class: () => "cbtn toggle" + (knowledge.useInChat.val ? " on" : ""),
            onclick: knowledge.toggleUseInChat,
          },
          Icon("file", { size: 14, strokeWidth: 2 }),
          "Use my docs",
        ),
        button(
          {
            class: "send",
            title: "Send",
            disabled: () => chat.streaming.val,
            onclick: submit,
          },
          Icon("send", { size: 17, strokeWidth: 2.2 }),
        ),
      ),
    ),
    div(
      { class: "chint" },
      "Gert can search the web, run Python in a gVisor sandbox, and read your private documents. It can make mistakes — check sources.",
    ),
  );
};
