// components/main/composer.js — autogrow textarea + attach + tools dropdown +
// send + hint. Calls services/chat.send; never fetches directly.
import van from "van";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { ToolsMenu } from "./tools-menu.js";
import { ContextRing } from "./context-ring.js";
import * as chatSvc from "../../services/chat.js";
import * as chat from "../../state/chat.js";
import * as docsSvc from "../../services/documents.js";
import { attempt } from "../../lib/action.js";

const { div, textarea, button } = van.tags;

const autogrow = (t) => {
  t.style.height = "auto";
  t.style.height = Math.min(t.scrollHeight, 160) + "px";
};

export const Composer = component({
  name: "composer",
  css: `
    .composer-wrap{padding:14px 30px 22px; background:var(--paper); position:relative; z-index:1;}
    /* fade strip: hangs 48px ABOVE the wrap, over the stream's last lines —
       solid paper at its base (exactly where the stream clips, hiding the hard
       edge) fading to transparent upward. Click-through; the stream keeps its
       own scroll/clicks. Stream bottom padding (message-stream.js) keeps a
       bottom-pinned message clear of the strip. */
    .composer-wrap::before{content:""; position:absolute; left:0; right:0; bottom:100%; height:48px; background:linear-gradient(to top, var(--paper), transparent); pointer-events:none;}
    .composer{max-width:760px; margin:0 auto; background:var(--surface); border:1px solid var(--line-strong); border-radius:16px; padding:13px 15px 11px; box-shadow:0 6px 22px -12px rgba(60,46,28,.22); transition:.16s;}
    .composer:focus-within{border-color:var(--accent); box-shadow:0 8px 26px -12px rgba(191,71,39,.32);}
    .composer textarea{width:100%; border:none; background:none; resize:none; outline:none; font-family:var(--sans); font-size:15px; color:var(--ink); line-height:1.5; min-height:24px;}
    .composer textarea::placeholder{color:var(--ink-faint);}
    .crow{display:flex; align-items:center; gap:9px; margin-top:9px;}
    .cbtn{background:none; border:1px solid var(--line-strong); border-radius:8px; padding:6px 10px; cursor:pointer; color:var(--ink-soft); font-family:var(--sans); font-size:12px; font-weight:500; display:flex; align-items:center; gap:6px; transition:.14s;}
    .cbtn:hover{border-color:var(--accent); color:var(--accent-deep); background:var(--accent-soft);}
    .cbtn svg{width:14px; height:14px;}
    .cbtn.toggle.on{border-color:var(--sage-soft); color:var(--sage); background:var(--sage-soft);}
    /* right cluster: context ring + send/stop pinned to the corner */
    .cright{margin-left:auto; display:flex; align-items:center; gap:9px;}
    .send{width:36px; height:36px; border-radius:10px; border:none; background:var(--accent); color:var(--on-accent); cursor:pointer; display:grid; place-items:center; transition:.16s;}
    .send:hover{background:var(--accent-deep); transform:translateY(-1px);}
    /* empty textbox → inert grey button (no faded-accent look) */
    .send:disabled{background:var(--line-strong); color:var(--ink-faint); cursor:default; transform:none;}
    .send:disabled:hover{background:var(--line-strong);}
    .send svg{width:17px; height:17px;}
    /* shown while the turn streams — click to detach (stop) */
    .stop{width:36px; height:36px; border-radius:10px; border:none; background:var(--accent); color:var(--on-accent); cursor:pointer; display:grid; place-items:center; transition:.16s; animation:pulse 1.4s ease-in-out infinite;}
    .stop:hover{background:var(--accent-deep);}
    .stop svg{width:15px; height:15px;}
  `,
  view: () => {
    // ── logic ───────────────────────────────────
    // mirror the textarea's emptiness reactively so the send button can grey out.
    const empty = van.state(true);

    const ta = textarea({
      rows: 1,
      placeholder: "Message Gert…  ⌘↵ to send",
      oninput: (e) => {
        autogrow(e.target);
        empty.val = !e.target.value.trim();
      },
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
      empty.val = true;
      autogrow(ta);
    };

    const fileInput = van.tags.input({
      type: "file",
      style: "display:none",
      onchange: (e) => {
        const f = e.target.files?.[0];
        if (f) attempt(() => docsSvc.upload(f), "Upload failed");
        e.target.value = "";
      },
    });

    // ── content ─────────────────────────────────
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
          ToolsMenu(),
          // reasoning toggle — persisted per conversation on the next send
          button(
            {
              class: () => "cbtn toggle" + (chat.thinking.val ? " on" : ""),
              title: "Model reasoning (thinking) on/off",
              onclick: () => (chat.thinking.val = !chat.thinking.val),
            },
            Icon("brain", { size: 14, strokeWidth: 2 }),
            "Thinking",
          ),
          // bottom-right: context ring, then Send/Stop
          div(
            { class: "cright" },
            ContextRing(),
            () =>
              chat.streaming.val
                ? button(
                    { class: "stop", title: "Stop", onclick: chatSvc.stop },
                    Icon("stop", { size: 15, strokeWidth: 0 }),
                  )
                : button(
                    {
                      class: "send",
                      title: "Send",
                      disabled: () => empty.val,
                      onclick: submit,
                    },
                    Icon("send", { size: 17, strokeWidth: 2.2 }),
                  ),
          ),
        ),
      ),
    );
  },
});
