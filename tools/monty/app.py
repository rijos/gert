"""app.py - the Gert monty sandbox sidecar.

Wraps Pydantic Monty (https://github.com/pydantic/monty - a minimal Python interpreter
in Rust with no syscalls) behind the tiny HTTP contract the .NET ``MontySandbox`` adapter
speaks:

    POST /run  { code, wall_clock_seconds, memory_mib, max_output_bytes }
            -> { stdout, stderr, exit_code, timed_out }

Run it (no container needed)::

    cd tools/monty && uv run python app.py        # listens on 127.0.0.1:8077

Then point Gert at it with ``Gert:Tools:Sandbox:Type=Monty`` and
``Gert:Tools:Sandbox:Parameters:BaseUrl=http://127.0.0.1:8077`` (both are the defaults).

Lock it down at deploy time the way Gert expects (security F5): run as an unprivileged
user, with NO mount of the per-user data root and egress off. Monty itself has no
filesystem, network, or env access - this process boundary is the OS belt around that.
"""

from __future__ import annotations

import os
from typing import Any

import pydantic_monty
import uvicorn
from starlette.applications import Starlette
from starlette.concurrency import run_in_threadpool
from starlette.requests import Request
from starlette.responses import JSONResponse
from starlette.routing import Route

# Monty's default call-stack ceiling; kept modest for LLM-written snippets.
DEFAULT_RECURSION_DEPTH = 500


def _run_monty(
    code: str, wall_clock_seconds: int, memory_mib: int, max_output_bytes: int
) -> tuple[str, str, int, bool]:
    """Execute ``code`` in monty, capturing printed output via the print callback.

    Returns ``(stdout, stderr, exit_code, timed_out)``. Monty enforces the duration and
    memory ceilings itself and raises ``MontyError`` (syntax/runtime/typing/limit); we
    render that to stderr with a non-zero exit so the model reads the failure and adapts.
    """
    chunks: list[str] = []

    def print_callback(stream: str, text: str) -> None:
        chunks.append(text)

    limits = pydantic_monty.ResourceLimits(
        max_duration_secs=float(wall_clock_seconds),
        max_memory=memory_mib * 1024 * 1024,
        max_recursion_depth=DEFAULT_RECURSION_DEPTH,
    )

    try:
        monty = pydantic_monty.Monty(code)
        monty.run(print_callback=print_callback, limits=limits)
    except pydantic_monty.MontyError as exc:
        rendered = exc.display(format="traceback")
        # Best-effort: a duration-limit trip reads back as a timeout on the tool card.
        timed_out = "duration" in rendered.lower() or "time limit" in rendered.lower()
        return (
            "".join(chunks)[:max_output_bytes],
            rendered[:max_output_bytes],
            1,
            timed_out,
        )

    return "".join(chunks)[:max_output_bytes], "", 0, False


async def run(request: Request) -> JSONResponse:
    payload: dict[str, Any] = await request.json()
    raw_code = payload.get("code", "")
    if not isinstance(raw_code, str):
        return JSONResponse(
            {"stdout": "", "stderr": "missing 'code'", "exit_code": 1, "timed_out": False}
        )

    wall = int(payload.get("wall_clock_seconds", 10))
    memory = int(payload.get("memory_mib", 256))
    cap = int(payload.get("max_output_bytes", 64 * 1024))

    # Monty's run is synchronous and CPU-bound (and self-limited by max_duration_secs),
    # so keep it off the event loop.
    stdout, stderr, exit_code, timed_out = await run_in_threadpool(
        _run_monty, raw_code, wall, memory, cap
    )
    return JSONResponse(
        {
            "stdout": stdout,
            "stderr": stderr,
            "exit_code": exit_code,
            "timed_out": timed_out,
        }
    )


def create_app() -> Starlette:
    return Starlette(routes=[Route("/run", run, methods=["POST"])])


app = create_app()


def main() -> None:
    host = os.environ.get("GERT_MONTY_HOST", "127.0.0.1")
    port = int(os.environ.get("GERT_MONTY_PORT", "8077"))
    uvicorn.run(app, host=host, port=port, log_level="info")


if __name__ == "__main__":
    main()
