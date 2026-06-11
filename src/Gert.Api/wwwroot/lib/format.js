// format.js — tiny shared number formatters (no deps, no DOM).

// Byte counts for doc/user size labels: KB below 1 MB, then MB / GB to one
// decimal. The single owner — was triplicated across doc-row / knowledge-panel
// / admin users before.
export const fmtBytes = (bytes) => {
  const n = bytes || 0;
  if (n >= 1_073_741_824) return (n / 1_073_741_824).toFixed(1) + " GB";
  if (n >= 1_048_576) return (n / 1_048_576).toFixed(1) + " MB";
  return Math.round(n / 1024) + " KB";
};

// Compact counts (token totals): 1500 → "1.5K", 2000 → "2K".
export const fmtK = (n) =>
  n >= 1000 ? (n / 1000).toFixed(1).replace(/\.0$/, "") + "K" : String(n);

// Relative timestamps for activity columns ("2 h ago", "3 days ago"); falls
// back to a localized date past a week, and to the raw string when unparsable.
// Callers keep the ISO string in a title attribute for the precise value.
export const fmtRelative = (iso) => {
  const t = Date.parse(iso || "");
  if (Number.isNaN(t)) return iso || "—";
  const s = Math.max(0, (Date.now() - t) / 1000);
  const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: "auto", style: "narrow" });
  if (s < 60) return "just now";
  if (s < 3600) return rtf.format(-Math.round(s / 60), "minute");
  if (s < 86_400) return rtf.format(-Math.round(s / 3600), "hour");
  if (s < 7 * 86_400) return rtf.format(-Math.round(s / 86_400), "day");
  return new Date(t).toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
};
