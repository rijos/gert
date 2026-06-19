// Pure date-grouping helpers for the conversation list. Kept beside convo-list
// (its only consumer) rather than in lib/.

const DAY = 86_400_000;

const startOfDay = (d: Date) =>
  new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();

// Calendar-date grouping (not age): a chat from 11 pm yesterday is
// "Yesterday" at 8 am today, whatever the hour gap says.
export const groupOf = (updatedAt: string | undefined) => {
  const t = new Date(updatedAt || NaN);
  if (Number.isNaN(t.getTime())) return "Earlier";
  const now = new Date();
  const today = startOfDay(now);
  const day = startOfDay(t);
  if (day >= today) return "Today";
  if (day >= today - DAY) return "Yesterday";
  return t.toLocaleDateString(undefined, {
    month: "long",
    day: "numeric",
    ...(t.getFullYear() !== now.getFullYear() ? { year: "numeric" } : {}),
  });
};

// Today, Yesterday, then date groups in list order (newest first); the
// undated fallback sinks to the bottom.
export const rank = (g: string) =>
  g === "Today" ? 0 : g === "Yesterday" ? 1 : g === "Earlier" ? 3 : 2;
