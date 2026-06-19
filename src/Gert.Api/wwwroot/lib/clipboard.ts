// lib/clipboard.ts - clipboard write with a selection-API fallback for
// non-secure contexts (no navigator.clipboard). Shared by the message
// code-block copy button, the message actions row, and the composer.
export const copyText = (text: string) =>
  (navigator.clipboard?.writeText(text) ?? Promise.reject()).catch(() => {
    const ta = document.createElement("textarea");
    ta.value = text;
    document.body.appendChild(ta);
    ta.select();
    document.execCommand("copy");
    ta.remove();
  });
