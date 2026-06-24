// components/main/composer.js - autogrow textarea + attach + tools dropdown +
// send + hint. Pasted/dropped/picked files queue as pending INLINE attachments (thumbnail
// strip for images, a chip for text files) and ride the next send: an image as a vision part,
// a text file as full inline text (bounded by the model's context). The knowledge-base upload
// is the canvas drop-zone, not here. Calls services/chat.send; never fetches directly.
import van from "/lib/van.js";
import type { State } from "/lib/van.js";
import { reactive } from "/lib/van-x.js";
import { component } from "../../lib/component.js";
import { Icon } from "../../icons/icons.js";
import { ToolsMenu } from "./tools-menu.js";
import { ContextRing } from "./context-ring.js";
import * as chatSvc from "../../services/chat.js";
import * as chat from "../../state/chat.js";
import type { Attachment } from "../../state/chat.js";
import * as ui from "../../state/ui.js";
import { selected } from "../../state/models.js";
import { attempt } from "../../lib/action.js";
import { toast } from "../ui/toast.js";
import { t } from "../../lib/i18n.js";

const { div, textarea, button, img, span } = van.tags;

const autogrow = (t: HTMLTextAreaElement) => {
  t.style.height = "auto";
  t.style.height = Math.min(t.scrollHeight, 160) + "px";
};

const MAX_IMAGES = 6; // server cap (attachments.too_many)
const MAX_DIM = 1568; // longest edge sent upstream
const KEEP_BYTES = 512 * 1024; // small originals keep their exact bytes
// Inline text-file budget: must mirror the server's TurnOptions.MaxInlineAttachmentContextFraction
// (default 0.5) and its chars/token estimate, so the client pre-check and the server gate agree.
const MAX_INLINE_CONTEXT_FRACTION = 0.5;
const CHARS_PER_TOKEN = 4;

const readAsDataUrl = (blob: Blob): Promise<string> =>
  new Promise((res, rej) => {
    const r = new FileReader();
    // readAsDataURL always yields a data: URL string in r.result on load.
    r.onload = () => res(r.result as string);
    r.onerror = () => rej(r.error);
    r.readAsDataURL(blob);
  });

// A pending attachment: the wire fields (Attachment) plus, for an image, the preview
// data URL the thumbnail strip binds to. A text file has no `url` (it renders as a chip).
interface PendingAttachment extends Attachment {
  url?: string;
}

// Bound a pasted image for the wire: a small original keeps its exact bytes
// (and mime); anything big is downscaled to MAX_DIM and re-encoded as JPEG
// over a white matte (transparency would otherwise go black). Returns
// { mime_type, data, url } - wire fields + the preview data URL - or null
// when the blob can't be decoded as an image.
const processImage = async (file: File): Promise<PendingAttachment | null> => {
  let bmp: ImageBitmap;
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
  // 2d context: only null when a different context type was already requested
  // on this freshly created canvas - never here.
  const ctx = canvas.getContext("2d")!;
  ctx.fillStyle = "#fff";
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  ctx.drawImage(bmp, 0, 0, canvas.width, canvas.height);
  bmp.close?.();
  const url = canvas.toDataURL("image/jpeg", 0.85);
  return { mime_type: "image/jpeg", data: url.slice(url.indexOf(",") + 1), url };
};

// Bound a dropped text file for the wire: its bytes ride inline (base64) with the filename,
// no preview url (it renders as a downloadable chip). Read via a data URL so any encoding maps
// to base64 uniformly; the server decodes + gates text-ness.
const processTextFile = async (file: File): Promise<PendingAttachment> => {
  const url = await readAsDataUrl(file);
  return {
    mime_type: file.type || "text/plain",
    data: url.slice(url.indexOf(",") + 1),
    name: file.name,
  };
};

export const Composer = component({
  name: "composer",
  css: `
    .composer-wrap {
      padding: 14px 30px 22px;
      background: var(--bg);
      position: relative;
      z-index: 1;
    }
    /* fade strip: hangs 48px ABOVE the wrap, over the stream's last lines -
       solid paper at its base (exactly where the stream clips, hiding the hard
       edge) fading to transparent upward. Click-through; the stream keeps its
       own scroll/clicks. Stream bottom padding (message-stream.js) keeps a
       bottom-pinned message clear of the strip. */
    .composer-wrap::before {
      content: "";
      position: absolute;
      left: 0;
      right: 0;
      bottom: 100%;
      height: 48px;
      background: linear-gradient(to top, var(--bg), transparent);
      pointer-events: none;
    }
    .composer {
      max-width: 760px;
      margin: 0 auto;
      background: var(--surface);
      border: 1px solid var(--line);
      border-radius: var(--r-lg);
      padding: 13px 15px 11px;
      box-shadow: var(--lift);
      transition: var(--t-fast);
    }
    .composer:focus-within {
      border-color: var(--coral-line);
      box-shadow: var(--lift), 0 0 0 3px var(--coral-soft);
    }
    /* file dragged over the composer: same affordance as focus, plus a dashed cue */
    .composer-wrap.drag-over .composer {
      border-color: var(--coral-line);
      border-style: dashed;
      box-shadow: var(--lift), 0 0 0 3px var(--coral-soft);
    }
    /* outline:none opts out of the global :focus-visible ring - the
       :focus-within treatment above paints the focus state on the container */
    .composer textarea {
      width: 100%;
      border: none;
      background: none;
      resize: none;
      outline: none;
      font-family: var(--sans);
      font-size: var(--fs-base);
      color: var(--ink);
      line-height: var(--lh-ui);
      min-height: 24px;
    }
    .composer textarea::placeholder {
      color: var(--ink-3);
    }
    .crow {
      display: flex;
      align-items: center;
      gap: 9px;
      margin-top: 9px;
    }
    .cbtn {
      background: none;
      border: 1px solid var(--line);
      border-radius: var(--r-sm);
      padding: 6px 10px;
      cursor: pointer;
      color: var(--ink-2);
      font-family: var(--sans);
      font-size: var(--fs-sm);
      font-weight: 500;
      display: flex;
      align-items: center;
      gap: 6px;
      transition: var(--t-fast);
    }
    .cbtn:hover {
      border-color: var(--coral);
      color: var(--coral-deep);
      background: var(--coral-soft);
    }
    .cbtn svg {
      width: 14px;
      height: 14px;
    }
    /* "active" pills (Tools with a count, Thinking on): the one warm accent */
    .cbtn.toggle.on {
      border-color: var(--coral-line);
      color: var(--coral-deep);
      background: var(--coral-soft);
    }
    /* right cluster: context ring + send/stop pinned to the corner */
    .cright {
      margin-left: auto;
      display: flex;
      align-items: center;
      gap: 9px;
    }
    .send {
      width: 36px;
      height: 36px;
      border-radius: 10px;
      border: 1px solid var(--coral-line);
      background: var(--coral-soft);
      color: var(--coral-deep);
      cursor: pointer;
      display: grid;
      place-items: center;
      transition: var(--t-fast);
    }
    /* colour-only hover: a transform here moved the button under the cursor */
    .send:hover {
      background: var(--coral-deep);
      border-color: var(--coral-deep);
      color: var(--on-accent);
    }
    /* empty textbox -> inert grey button (no faded-accent look) */
    .send:disabled {
      background: var(--surface-2);
      border-color: var(--line);
      color: var(--ink-3);
      cursor: default;
    }
    .send:disabled:hover {
      background: var(--surface-2);
    }
    .send svg {
      width: 17px;
      height: 17px;
    }
    /* shown while the turn streams - click to detach (stop) */
    .stop {
      width: 36px;
      height: 36px;
      border-radius: 10px;
      border: none;
      background: var(--coral-deep);
      color: var(--on-accent);
      cursor: pointer;
      display: grid;
      place-items: center;
      transition: var(--t-fast);
      animation: pulse 1.4s ease-in-out infinite;
    }
    .stop:hover {
      background: var(--coral);
    }
    .stop svg {
      width: 15px;
      height: 15px;
    }
    /* pasted-image strip: thumbnails pending on the next send */
    .att-strip {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin-bottom: 9px;
    }
    .att-thumb {
      position: relative;
      width: 56px;
      height: 56px;
      border-radius: 10px;
      overflow: hidden;
      border: 1px solid var(--line);
      box-shadow: var(--lift);
    }
    .att-thumb img {
      width: 100%;
      height: 100%;
      object-fit: cover;
      display: block;
    }
    .att-file {
      position: relative;
      display: inline-flex;
      align-items: center;
      gap: 6px;
      max-width: 220px;
      height: 56px;
      box-sizing: border-box;
      padding: 0 30px 0 11px;
      border-radius: 10px;
      border: 1px solid var(--line);
      background: var(--surface);
      box-shadow: var(--lift);
      font-size: var(--fs-sm);
      color: var(--text);
    }
    .att-file span {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .att-x {
      position: absolute;
      top: 3px;
      right: 3px;
      width: 18px;
      height: 18px;
      border-radius: 50%;
      border: none;
      background: var(--scrim-chip);
      color: var(--on-scrim);
      font-size: var(--fs-sm);
      line-height: 1;
      cursor: pointer;
      display: grid;
      place-items: center;
      padding: 0;
      transition: var(--t-fast);
    }
    .att-x:hover {
      background: var(--scrim-chip-hover);
    }
  `,
  // logic: the reactive emptiness flag, the pending pasted-image queue, and the
  // pure (non-reactive) send-chord hint. The textarea/send DOM + the handlers
  // that close over it - and the DOM-scoped starter-chip derive - stay in view.
  setup: () => {
    // mirror the textarea's emptiness reactively so the send button can grey out.
    const empty = van.state(true);
    // attachments pasted/dropped into the composer, pending on the next send
    const pending = reactive<PendingAttachment[]>([]);
    // true while a file is dragged over the composer (drop-target highlight)
    const dragOver = van.state(false);

    // the chord hint reads Cmd+Enter only where the command key exists
    // (macOS/iOS); Ctrl+Enter elsewhere. userAgentData is a newer Navigator
    // field (not yet in the DOM lib); read it through a narrow optional shape.
    const nav = navigator as Navigator & { userAgentData?: { platform?: string } };
    const isMac = /mac|iphone|ipad|ipod/i.test(
      nav.userAgentData?.platform || navigator.platform || "",
    );
    const modChord = isMac ? "Cmd+Enter" : "Ctrl+Enter";

    return { empty, pending, modChord, dragOver };
  },
  view: ({
    empty,
    pending,
    modChord,
    dragOver,
  }: {
    empty: State<boolean>;
    pending: PendingAttachment[];
    modChord: string;
    dragOver: State<boolean>;
  }) => {
    // A dropped (or picked) file queues as a pending INLINE attachment on the next message
    // (not the knowledge base - that's the canvas drop-zone). An image becomes a vision part;
    // any other file rides as full inline text. A text file too big for the selected model's
    // context is refused here (the server re-checks) and the user is pointed at the Knowledge panel.
    const acceptFiles = (files: FileList | null | undefined) =>
      attempt(async () => {
        for (const f of [...(files || [])]) {
          if (pending.length >= MAX_IMAGES) continue;
          if (f.type.startsWith("image/")) {
            const image = await processImage(f);
            if (image) pending.push(image);
          } else {
            const ctx = selected.val?.context;
            if (ctx && f.size / CHARS_PER_TOKEN > ctx * MAX_INLINE_CONTEXT_FRACTION) {
              toast(
                t(`"{name}" is too large to attach inline - add it to the knowledge panel instead.`)
                  .replace("{name}", f.name),
                "err",
              );
              continue;
            }
            pending.push(await processTextFile(f));
          }
        }
      }, "Couldn't add that file");
    const ta = textarea({
      rows: 1,
      // The placeholder is a hint, not a name (WCAG 3.3.2): give the textarea a stable label.
      "aria-label": t("Message Gert..."),
      // the hint tracks the configurable send chord (settings -> "Send with"):
      // Enter sends by default; mod+Enter when the user prefers Enter = newline
      placeholder: () =>
        `${t("Message Gert...")}  ${ui.submitKey.val === "enter" ? "Enter" : modChord} ${t("to send")}`,
      oninput: (e: Event) => {
        const t = e.target as HTMLTextAreaElement;
        autogrow(t);
        empty.val = !t.value.trim();
      },
      onkeydown: (e: KeyboardEvent) => {
        if (e.key !== "Enter" || e.isComposing) return;
        // mod+Enter always sends; bare Enter sends in "enter" mode
        // (Shift+Enter stays a newline either way).
        const send =
          e.metaKey || e.ctrlKey ||
          (ui.submitKey.val === "enter" && !e.shiftKey && !e.altKey);
        if (send) {
          e.preventDefault();
          submit();
        }
      },
      // Pasted images queue as pending attachments; text pastes fall through
      // to the default insert. A mixed clipboard (screenshot tools often ship
      // image + html) attaches the image only.
      onpaste: (e: ClipboardEvent) => {
        const files = [...(e.clipboardData?.items || [])]
          .filter((i) => i.kind === "file" && i.type.startsWith("image/"))
          .map((i) => i.getAsFile())
          .filter((f): f is File => f != null);
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
        pending.map(({ mime_type, data, name }) => ({ mime_type, data, name: name ?? null })),
      );
      pending.length = 0;
      ta.value = "";
      empty.val = true;
      autogrow(ta);
    };

    const fileInput = van.tags.input({
      type: "file",
      multiple: true,
      style: "display:none",
      onchange: (e: Event) => {
        const input = e.target as HTMLInputElement;
        acceptFiles(input.files);
        input.value = "";
      },
    });

    const wrap = div({
        class: () => "composer-wrap" + (dragOver.val ? " drag-over" : ""),
        // Drop a file anywhere on the composer: it queues as an inline attachment for the
        // next message (image -> vision part, other -> inline text) via acceptFiles.
        ondragover: (e: DragEvent) => {
          e.preventDefault();
          dragOver.val = true;
        },
        ondragleave: () => {
          dragOver.val = false;
        },
        ondrop: (e: DragEvent) => {
          e.preventDefault();
          dragOver.val = false;
          acceptFiles(e.dataTransfer?.files);
        },
      },
      div({ class: "composer" },
        () => pending.length
          ? div({ class: "att-strip" },
              ...pending.map((p, i) =>
                p.url
                  ? div({ class: "att-thumb" },
                      img({ src: p.url, alt: "pasted image" }),
                      button({ class: "att-x", title: t("Remove"), "aria-label": t("Remove"), onclick: () => pending.splice(i, 1) }, "x"),
                    )
                  : div({ class: "att-file" },
                      Icon("file", { size: 14, strokeWidth: 2 }),
                      span(p.name || t("file")),
                      button({ class: "att-x", title: t("Remove"), "aria-label": t("Remove"), onclick: () => pending.splice(i, 1) }, "x"),
                    ),
              ),
            )
          : div(),
        ta,
        div({ class: "crow" },
          button({ class: "cbtn", onclick: () => fileInput.click() },
            Icon("attach", { size: 14, strokeWidth: 2 }),
            t("Attach"),
          ),
          fileInput,
          ToolsMenu(),
          div({ class: "cright" },
            ContextRing(),
            () => chat.streaming.val
              ? button({ class: "stop", title: t("Stop"), "aria-label": t("Stop"), onclick: chatSvc.stop }, Icon("stop", { size: 15, strokeWidth: 0 }))
              : button({ class: "send", title: t("Send"), "aria-label": t("Send"), disabled: () => empty.val && !pending.length, onclick: submit },
                  Icon("send", { size: 17, strokeWidth: 2.2 }),
                ),
          ),
        ),
      ),
    );

    // starter-chip hand-off (chat.draft, written by the empty-thread hero):
    // consume the prompt into the textarea and reset the signal. Scoped to
    // `wrap` so the derive is pruned with the component (section 12).
    // van.derive's vendored .d.ts types only the 1-arg form; the runtime also
    // takes (state, dom) for scope pruning (vanjs-core 1.6.0). Cast names the
    // real 3-arg shape at this call - runtime is byte-identical.
    (van.derive as (f: () => void, s: undefined, dom: Element) => unknown)(
      () => {
        const text = chat.draft.val;
        if (!text) return;
        ta.value = text;
        empty.val = false;
        autogrow(ta);
        ta.focus();
        queueMicrotask(() => (chat.draft.val = ""));
      },
      undefined,
      wrap,
    );

    return wrap;
  },
});
