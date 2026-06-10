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
