"""vllm.py - OpenAI-compatible vLLM mock (testing.md section 4.2 / A.3).

A Starlette ASGI app the *real* ``Gert.Chat`` vLLM adapter points at in the
FakeE2E profile. Two endpoints:

* ``POST /v1/chat/completions`` - streaming SSE in the OpenAI wire format
  (``data: {chunk}\\n\\n``, ``delta.content`` / ``delta.tool_calls``, a
  ``finish_reason`` chunk, a trailing usage-only chunk, then ``[DONE]``). The
  reply is resolved from the shared fixtures by the last user message; if the
  request messages already carry a ``tool`` role message, the follow-up
  ``after_tool`` reply is played (the tool-loop second call).
* ``POST /v1/embeddings`` - deterministic vectors via ``specs.embed`` in the
  OpenAI ``{"data":[{"index","embedding"}]}`` shape the adapter parses.

The chunk shapes match exactly what ``OpenAIStreamParser`` consumes, so the
``ChatEvent`` stream the browser renders is the same one the .NET fakes produce.
"""

from __future__ import annotations

import asyncio
import json
import time
from collections.abc import AsyncIterator
from typing import Any

from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import JSONResponse, StreamingResponse
from starlette.routing import Route

from . import specs

MODEL_ID = "default"


def _last_user_message(messages: list[dict[str, Any]]) -> str:
    for m in reversed(messages):
        if m.get("role") == "user":
            return m.get("content") or ""
    return ""


def _has_tool_result(messages: list[dict[str, Any]]) -> bool:
    """True when the request carries a tool-role message (the follow-up call)."""
    return any(m.get("role") == "tool" for m in messages)


def _sse(obj: dict[str, Any]) -> bytes:
    return f"data: {json.dumps(obj)}\n\n".encode()


async def chat_completions(request: Request) -> StreamingResponse:
    body = await request.json()
    messages = body.get("messages", [])
    last_user = _last_user_message(messages)
    after_tool = _has_tool_result(messages)
    reply = specs.resolve_completion(last_user, after_tool=after_tool)

    created = int(time.time())
    completion_id = f"chatcmpl-{created}"

    def base_chunk(
        choices: list[dict[str, Any]], usage: dict[str, Any] | None = None
    ) -> dict[str, Any]:
        chunk = {
            "id": completion_id,
            "object": "chat.completion.chunk",
            "created": created,
            "model": MODEL_ID,
            "choices": choices,
        }
        if usage is not None:
            chunk["usage"] = usage
        return chunk

    async def stream() -> AsyncIterator[bytes]:
        # 0) Reasoning deltas BEFORE tool calls/content - what
        # --reasoning-parser qwen3 emits. The field is `reasoning` (the vLLM
        # 0.22 output name; the parser also accepts the legacy
        # `reasoning_content`). Suppressed when the request sent
        # chat_template_kwargs.enable_thinking=false (the thinking toggle).
        template_kwargs = body.get("chat_template_kwargs") or {}
        thinking_enabled = template_kwargs.get("enable_thinking", True)
        if thinking_enabled:
            for thought in reply.get("reasoning_deltas", []):
                if not thought:
                    continue
                yield _sse(
                    base_chunk(
                        [
                            {
                                "index": 0,
                                "delta": {"reasoning": thought},
                                "finish_reason": None,
                            }
                        ]
                    )
                )

        # 1) Tool call (whole call in one delta: id + function.name + arguments).
        tool_call = reply.get("tool_call")
        if tool_call is not None:
            yield _sse(
                base_chunk(
                    [
                        {
                            "index": 0,
                            "delta": {
                                "tool_calls": [
                                    {
                                        "index": 0,
                                        "id": f"call_{tool_call['name']}",
                                        "type": "function",
                                        "function": {
                                            "name": tool_call["name"],
                                            "arguments": tool_call.get(
                                                "arguments", "{}"
                                            ),
                                        },
                                    }
                                ]
                            },
                            "finish_reason": None,
                        }
                    ]
                )
            )

        # 2) Content deltas, one SSE chunk per element. delay_ms (the slow
        # fixture) paces them so the resume E2E can reload mid-stream.
        delay_s = reply.get("delay_ms", 0) / 1000.0
        for delta in reply.get("deltas", []):
            if not delta:
                continue
            if delay_s:
                await asyncio.sleep(delay_s)
            yield _sse(
                base_chunk(
                    [{"index": 0, "delta": {"content": delta}, "finish_reason": None}]
                )
            )

        # 3) Terminal finish chunk (finish_reason on the last choice, empty delta).
        yield _sse(
            base_chunk(
                [
                    {
                        "index": 0,
                        "delta": {},
                        "finish_reason": reply.get("finish", "stop"),
                    }
                ]
            )
        )

        # 4) Trailing usage-only chunk (vLLM with stream_options.include_usage).
        # prompt_tokens ~ chars/4 so the SPA's context ring shows real movement.
        usage = reply.get("usage") or {}
        prompt_tokens = (
            sum(
                len(m.get("content") or "") + len(m.get("reasoning_content") or "")
                for m in messages
            )
            // 4
        )
        completion_tokens = usage.get("completion_tokens", 0)
        yield _sse(
            base_chunk(
                [],
                usage={
                    "prompt_tokens": prompt_tokens,
                    "completion_tokens": completion_tokens,
                    "total_tokens": prompt_tokens + completion_tokens,
                },
            )
        )

        yield b"data: [DONE]\n\n"

    return StreamingResponse(stream(), media_type="text/event-stream")


async def embeddings(request: Request) -> JSONResponse:
    body = await request.json()
    raw = body.get("input", [])
    inputs = [raw] if isinstance(raw, str) else list(raw)

    data = [
        {"object": "embedding", "index": i, "embedding": specs.embed(text)}
        for i, text in enumerate(inputs)
    ]
    return JSONResponse(
        {
            "object": "list",
            "model": body.get("model", "bge-m3"),
            "data": data,
            "usage": {"prompt_tokens": 0, "total_tokens": 0},
        }
    )


def create_app() -> Starlette:
    return Starlette(
        routes=[
            Route("/v1/chat/completions", chat_completions, methods=["POST"]),
            Route("/v1/embeddings", embeddings, methods=["POST"]),
        ]
    )


app = create_app()
