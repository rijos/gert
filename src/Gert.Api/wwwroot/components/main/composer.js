// components/main/composer.js — autogrow textarea + attach + tools dropdown +
// send + hint. Pasted images queue as pending attachments (thumbnail strip)
// and ride the next send for vision models. Calls services/chat.send; never
// fetches directly.
import van from "van";
import { reactive } from "van-x";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { ToolsMenu } from "./tools-menu.js";
import { ContextRing } from "./context-ring.js";
import * as chatSvc from "../../services/chat.js";
import * as chat from "../../state/chat.js";
import * as docsSvc from "../../services/documents.js";
import { attempt } from "../../lib/action.js";

const { div, textarea, button, img } = van.tags;

const autogrow = (t) => {
  t.style.height = "auto";
  t.style.height = Math.min(t.scrollHeight, 160) + "px";
};

// --- pasted-image processing --------------------------------------------------
const MAX_IMAGES = 6; // server cap (attachments.too_many)
const MAX_DIM = 1568; // longest edge sent upstream
const KEEP_BYTES = 512 * 1024; // small originals keep their exact bytes

const readAsDataUrl = (blob) =>
  new Promise((res, rej) => {
    const r = new FileReader();
    r.onload = () => res(r.result);
    r.onerror = () => rej(r.error);
    r.readAsDataURL(blob);
  });

// Bound a pasted image for the wire: a small original keeps its exact bytes
// (and mime); anything big is downscaled to MAX_DIM and re-encoded as JPEG
// over a white matte (transparency would otherwise go black). Returns
// { mime_type, data, url } — wire fields + the preview data URL — or null
// when the blob can't be decoded as an image.
const processImage = async (file) => {
  let bmp;
  try {
    bmp = await createImageBitmap(file);
  } catch {
    return null;
  }
  const scale = Math.min(1, MAX_DIM / Math.max(bmp.width, bmp.height));
  if (scale === 1 && file.size <= KEEP_BYTES) {
    bmp.close?.();
    const url = await readAsDataUrl(file);
    return { mime_type: file.type, data: url.slice(url.indexOf(",") + 1), url };
  }
  const canvas = document.createElement("canvas");
  canvas.width = Math.max(1, Math.round(bmp.width * scale));
  canvas.height = Math.max(1, Math.round(bmp.height * scale));
  const ctx = canvas.getContext("2d");
  ctx.fillStyle = "#fff";
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  ctx.drawImage(bmp, 0, 0, canvas.width, canvas.height);
  bmp.close?.();
  const url = canvas.toDataURL("image/jpeg", 0.85);
  return { mime_type: "image/jpeg", data: url.slice(url.indexOf(",") + 1), url };
};

export const Composer = component({
  name: "composer",
  css: `
    .composer-wrap{padding:14px 30px 22px; background:var(--bg); position:relative; z-index:1;}
    /* fade strip: hangs 48px ABOVE the wrap, over the stream's last lines —
       solid paper at its base (exactly where the stream clips, hiding the hard
       edge) fading to transparent upward. Click-through; the stream keeps its
       own scroll/clicks. Stream bottom padding (message-stream.js) keeps a
       bottom-pinned message clear of the strip. */
    .composer-wrap::before{content:""; position:absolute; left:0; right:0; bottom:100%; height:48px; background:linear-gradient(to top, var(--bg), transparent); pointer-events:none;}
    .composer{max-width:760px; margin:0 auto; background:var(--surface); border:1px solid var(--line); border-radius:16px; padding:13px 15px 11px; box-shadow:var(--lift); transition:.16s;}
    .composer:focus-within{border-color:var(--coral-line); box-shadow:var(--lift), 0 0 0 3px var(--coral-soft);}
    .composer textarea{width:100%; border:none; background:none; resize:none; outline:none; font-family:var(--sans); font-size:15px; color:var(--ink); line-height:1.5; min-height:24px;}
    .composer textarea::placeholder{color:var(--ink-3);}
    .crow{display:flex; align-items:center; gap:9px; margin-top:9px;}
    .cbtn{background:none; border:1px solid var(--line); border-radius:8px; padding:6px 10px; cursor:pointer; color:var(--ink-2); font-family:var(--sans); font-size:12px; font-weight:500; display:flex; align-items:center; gap:6px; transition:.14s;}
    .cbtn:hover{border-color:var(--coral); color:var(--coral-deep); background:var(--coral-soft);}
    .cbtn svg{width:14px; height:14px;}
    /* "active" pills (Tools with a count, Thinking on): green treatment */
    .cbtn.toggle.on{border-color:var(--green-line); color:var(--green); background:var(--green-soft);}
    /* right cluster: context ring + send/stop pinned to the corner */
    .cright{margin-left:auto; display:flex; align-items:center; gap:9px;}
    .send{width:36px; height:36px; border-radius:10px; border:1px solid var(--coral-line); background:var(--coral-soft); color:var(--coral); cursor:pointer; display:grid; place-items:center; transition:.16s;}
    .send:hover{background:var(--coral); color:var(--on-accent); transform:translateY(-1px);}
    /* empty textbox → inert grey button (no faded-accent look) */
    .send:disabled{background:var(--surface-2); border-color:var(--line); color:var(--ink-3); cursor:default; transform:none;}
    .send:disabled:hover{background:var(--surface-2);}
    .send svg{width:17px; height:17px;}
    /* shown while the turn streams — click to detach (stop) */
    .stop{width:36px; height:36px; border-radius:10px; border:none; background:var(--coral); color:var(--on-accent); cursor:pointer; display:grid; place-items:center; transition:.16s; animation:pulse 1.4s ease-in-out infinite;}
    .stop:hover{background:var(--coral-deep);}
    .stop svg{width:15px; height:15px;}
    /* pasted-image strip: thumbnails pending on the next send */
    .att-strip{display:flex; flex-wrap:wrap; gap:8px; margin-bottom:9px;}
    .att-thumb{position:relative; width:56px; height:56px; border-radius:10px; overflow:hidden; border:1px solid var(--line); box-shadow:var(--lift);}
    .att-thumb img{width:100%; height:100%; object-fit:cover; display:block;}
    .att-x{position:absolute; top:3px; right:3px; width:18px; height:18px; border-radius:50%; border:none; background:rgba(0,0,0,.55); color:#fff; font-size:12px; line-height:1; cursor:pointer; display:grid; place-items:center; padding:0; transition:.13s;}
    .att-x:hover{background:rgba(0,0,0,.8);}
  `,
  view: () => {
    // ── logic ───────────────────────────────────
    // mirror the textarea's emptiness reactively so the send button can grey out.
    const empty = van.state(true);
    // images pasted into the textarea, pending on the next send
    const pending = reactive([]); // [{ mime_type, data, url }]

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
      // Pasted images queue as pending attachments; text pastes fall through
      // to the default insert. A mixed clipboard (screenshot tools often ship
      // image + html) attaches the image only.
      onpaste: (e) => {
        const files = [...(e.clipboardData?.items || [])]
          .filter((i) => i.kind === "file" && i.type.startsWith("image/"))
          .map((i) => i.getAsFile())
          .filter(Boolean);
        if (!files.length) return;
        e.preventDefault();
        attempt(async () => {
          for (const f of files.slice(0, MAX_IMAGES - pending.length)) {
            const image = await processImage(f);
            if (image) pending.push(image);
          }
        }, "Couldn't read the pasted image");
      },
    });

    const submit = () => {
      const text = ta.value;
      if ((!text.trim() && !pending.length) || chat.streaming.val) return;
      chatSvc.send(
        text,
        pending.map(({ mime_type, data }) => ({ mime_type, data })),
      );
      pending.length = 0;
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
        // pending pasted images (thumbnail strip with per-image remove)
        () =>
          pending.length
            ? div(
                { class: "att-strip" },
                ...pending.map((p, i) =>
                  div(
                    { class: "att-thumb" },
                    img({ src: p.url, alt: "pasted image" }),
                    button(
                      {
                        class: "att-x",
                        title: "Remove image",
                        onclick: () => pending.splice(i, 1),
                      },
                      "×",
                    ),
                  ),
                ),
              )
            : div(),
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
                      disabled: () => empty.val && !pending.length,
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
