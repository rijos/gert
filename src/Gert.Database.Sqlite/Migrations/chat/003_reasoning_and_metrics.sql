-- chat.db schema v3 — reasoning + generation metrics + per-conversation thinking.
-- Applied by SqliteMigrationRunner when PRAGMA user_version < 3.
--
-- * messages.reasoning: the model's thinking text (vLLM reasoning_content),
--   persisted on finalize so a reload restores the collapsed "Thinking" block.
--   NULL for user rows and for turns generated with thinking disabled.
-- * messages.duration_ms: pure generation wall-clock (stream-consumption spans
--   only; tool execution excluded) — the tokens/sec readout.
-- * messages.context_tokens: the context window occupied by the turn's final
--   model round (prompt_tokens + completion_tokens) — the composer's usage ring.
-- * conversations.thinking: tri-state reasoning preference (NULL = model
--   default, 0/1 = explicit off/on), mirroring the tools_json pattern so the
--   composer toggle survives a reload.
-- * conversations.preserve_thinking: tri-state — when on, prior turns'
--   reasoning is sent back upstream as assistant `reasoning_content` together
--   with chat_template_kwargs.preserve_thinking (Qwen3.6 interleaved thinking).

ALTER TABLE messages ADD COLUMN reasoning TEXT NULL;
ALTER TABLE messages ADD COLUMN duration_ms INTEGER NULL;
ALTER TABLE messages ADD COLUMN context_tokens INTEGER NULL;

ALTER TABLE conversations ADD COLUMN thinking INTEGER NULL;
ALTER TABLE conversations ADD COLUMN preserve_thinking INTEGER NULL;
