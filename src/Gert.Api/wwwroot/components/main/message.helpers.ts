// components/main/message.helpers.ts - pure helpers shared by the message body
// and the Sources card (sources.ts) + its letter avatar (avatar.ts): no van,
// no DOM, just value-returning string math.

// Only http(s) locators become links - same URL stance as the markdown
// renderer; anything else (document pages) renders as a plain row.
export const domainOf = (locator: string | null | undefined): string | null => {
  if (!locator || !/^https?:\/\//i.test(locator)) return null;
  try {
    return new URL(locator).hostname.replace(/^www\./, "");
  } catch {
    return null;
  }
};

// brand-ish letter avatar: first letter of the registrable domain, tinted
// deterministically from the domain so the same source always matches.
export const avatarHue = (key: string) => {
  let h = 0;
  // for..of over a string yields code points; codePointAt(0) on each is defined.
  for (const ch of key) h = (h * 31 + ch.codePointAt(0)!) % 360;
  return h;
};
