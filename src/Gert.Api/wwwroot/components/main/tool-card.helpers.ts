// Pure (no-van) helpers + runtime types for tool-card.ts. They return plain
// values, so they carry no reactivity.
import type { ToolCard as ToolCardRow } from "../../state/chat.js";
import type { ToolKind } from "../../services/wire.js";
import { availableTools } from "../../state/tools.js";

// One question (one tab) in a QuestionCard payload. `value` is the per-tab
// answer collected before submit.
export interface QuestionItem {
  text: string;
  header: string;
  options: string[];
  allowFreeText: boolean;
  value: string;
}

// QuestionCard's reactive payload, folded onto an ask_user card by
// services/chat.js: one to four questions rendered as tabs, answered together.
// The card mutates per-tab `value` plus `posting`/`answered`/`expired` in place,
// so the fields stay writable.
export interface Question {
  questionId: string;
  items: QuestionItem[];
  answered: boolean;
  answers: string[];
  expired: boolean;
  posting: boolean;
}

// The runtime tool-card entry on a message: the persisted ToolCard contract plus
// the live-stream `error` line and interactive `question` slot, both maintained at
// runtime by services/chat.js (`question` is null once resolved/absent).
export interface Card extends ToolCardRow {
  error?: string;
  question?: Question | null;
}

// One retrieval hit row off the wire (rag/search). All fields optional - the
// card renders whatever the tool returned.
export interface Hit {
  doc?: string;
  title?: string;
  page?: string | number;
  score?: number;
}

// The card's icon comes from the server catalog descriptor (state/tools), keyed by tool id - the
// same curated icons.ts key the menu uses, so cards and menu stay in lockstep with no hardcoded
// kind->icon map. Cards only render for entitled tools (which are in the catalog); a kind not in
// the catalog falls back to a neutral existing glyph.
export const iconFor = (kind: ToolKind): string =>
  availableTools.find((tool) => tool.id === kind)?.icon ?? "gear";

// done/total over a card's todos ([] for non-todo cards).
export const progress = (card: Card) => {
  const ts = card.todos || [];
  const done = ts.filter((t) => t.status === "done").length;
  return { ts, done, all: ts.length > 0 && done === ts.length };
};

// The todo card's live header label: the current step while the list is being
// worked, a quiet past-tense once every box is checked.
export const todoLabel = (card: Card) => {
  const { ts, all } = progress(card);
  if (all) return "Updated todo list";
  const active = ts.find((t) => t.status === "active");
  return active ? "Now: " + active.text : card.label || card.kind;
};
