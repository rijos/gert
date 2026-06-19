// lib/i18n.js - UI translation. The English source string IS the key
// (`t("New chat")`), so call sites stay readable and untranslated strings
// fall back to themselves. The language resolves once per page load:
// localStorage (the user's explicit choice) -> the browser's navigator.language
// -> English; switching in settings persists (localStorage + the server's
// ui_language) and reloads - components render strings once, so a live swap
// would leave half the chrome stale.
const LANG_KEY = "gert.lang";

export const AVAILABLE = [
  { value: "en", label: "English" },
  { value: "nl", label: "Nederlands" },
];

const NL = {
  // sidebar
  "New chat": "Nieuwe chat",
  "Chats": "Chats",
  "Projects": "Projecten",
  "Search all chats": "Zoek in alle chats",
  "Search all projects": "Zoek in alle projecten",
  "Close": "Sluiten",
  "Today": "Vandaag",
  "Yesterday": "Gisteren",
  "Earlier": "Eerder",
  "Untitled": "Naamloos",
  "Delete chat": "Chat verwijderen",
  "Move to project...": "Verplaats naar project...",
  "Move chat": "Chat verplaatsen",
  "Move": "Verplaatsen",
  "Settings": "Instellingen",
  "Admin panel": "Beheerpaneel",

  // projects
  "New project": "Nieuw project",
  "+ New project": "+ Nieuw project",
  "Project name": "Projectnaam",
  "Create": "Aanmaken",
  "Rename project": "Project hernoemen",
  "Rename": "Hernoemen",
  "Delete project": "Project verwijderen",
  "Delete": "Verwijderen",
  "Default": "Standaard",
  "Project": "Project",

  // composer
  "Message Gert...": "Bericht aan Gert...",
  "to send": "om te versturen",
  "Attach": "Bijlage",
  "Tools": "Tools",
  "Send": "Versturen",
  "Stop": "Stoppen",
  "Remove image": "Afbeelding verwijderen",

  // tools menu
  "Search": "Zoeken",
  "Fetch pages": "Pagina's ophalen",
  "Run Python": "Python uitvoeren",
  "Todos": "Taken",
  "Clock": "Klok",
  "Sub-agents": "Subagenten",
  "Ask me": "Vraag mij",
  "Save memories": "Herinneringen opslaan",
  "Canvas": "Canvas",
  "Use my docs": "Gebruik mijn documenten",

  // chat
  "What are we working on?": "Waar werken we aan?",
  "Jump to present": "Naar het heden",
  "New conversation": "Nieuw gesprek",

  // settings
  "Your preferences for Gert.": "Jouw voorkeuren voor Gert.",
  "Theme": "Thema",
  "Follow system": "Volg systeem",
  "Manila (paper)": "Manila (papier)",
  "Ember (dark)": "Ember (donker)",
  "Language": "Taal",
  "Send with": "Versturen met",
  "Default reply language": "Standaard antwoordtaal",
  "e.g. English": "bijv. Nederlands",
  "Memories": "Herinneringen",
  "Off - never store memories": "Uit - nooit herinneringen opslaan",
  "Manual - only when I ask": "Handmatig - alleen op verzoek",
  "Automatic - the model decides": "Automatisch - het model beslist",
  "Default model": "Standaardmodel",
  "Dials for the model above. Off = inherit the model's own defaults.":
    "Instellingen voor het model hierboven. Uit = de standaard van het model.",
  "Settings saved": "Instellingen opgeslagen",
  "Could not save settings": "Instellingen opslaan mislukt",
  "Model": "Model",

  // knowledge / memory
  "Knowledge": "Kennis",
  "Private to you - stored in your own file": "Privé - opgeslagen in je eigen bestand",
  "Use in this chat ": "Gebruik in deze chat ",
  "retrieve from your documents": "haal op uit je documenten",
  "Add": "Toevoegen",
  "New memory": "Nieuwe herinnering",
  "Title": "Titel",
  "Note": "Notitie",
  "What should Gert remember?": "Wat moet Gert onthouden?",
  "Pin - include in every chat": "Vastzetten - in elke chat meenemen",
  "Save": "Opslaan",
  "Nothing remembered yet.": "Nog niets onthouden.",
  "Delete memory": "Herinnering verwijderen",

  // search overlay
  "Search chats": "Chats zoeken",
  "Search your chats...": "Zoek in je chats...",
  "Search projects": "Projecten zoeken",
  "Search your projects...": "Zoek in je projecten...",
  "Nothing found.": "Niets gevonden.",
  "Searching...": "Zoeken...",
  "Loading...": "Laden...",

  // artifacts
  "Preview": "Voorbeeld",
  "Source": "Bron",
  "Download": "Downloaden",
  "All files": "Alle bestanden",
  "Files in this conversation": "Bestanden in dit gesprek",

  // shared
  "Cancel": "Annuleren",
  "OK": "OK",
};

// DICTS is keyed by the 2-letter language code resolved at runtime; each dict maps
// a UI source string to its translation.
const DICTS: Record<string, Record<string, string>> = { nl: NL };

const resolve = () => {
  const saved = localStorage.getItem(LANG_KEY);
  if (saved && DICTS[saved]) return saved;
  if (saved === "en") return "en";
  return (navigator.language || "en").toLowerCase().startsWith("nl") ? "nl" : "en";
};

const current = resolve();

// The active language for this page load ("en" | "nl").
export const lang = () => current;

// Translate one UI string; English (the key) is the fallback.
export const t = (text: string) => DICTS[current]?.[text] ?? text;

// Persist an explicit choice and reload - strings render once per load.
export const setLang = (next: string) => {
  const value = DICTS[next] || next === "en" ? next : "en";
  if (value === current) return;
  localStorage.setItem(LANG_KEY, value);
  location.reload();
};

// Server settings -> language cache (configuration.md section 3: ui_language is the
// cross-device truth; localStorage is the first-paint cache). No reload here -
// boot must not loop; the cached value applies from the next load on.
export const applyServerLanguage = (wire: unknown) => {
  if (!wire) return;
  const value = String(wire).toLowerCase().slice(0, 2);
  if ((DICTS[value] || value === "en") && !localStorage.getItem(LANG_KEY)) {
    localStorage.setItem(LANG_KEY, value);
  }
};
