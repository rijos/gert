"""__main__.py — boot all mock upstreams on localhost (one process).

Runs the vLLM and SearXNG mocks concurrently under uvicorn, on the ports the
FakeE2E launch profile points the real adapters at::

    uv run python -m tools.smoke.mocks            # both, default ports
    uv run python -m tools.smoke.mocks --vllm-port 8000 --searxng-port 8080

The launcher (``run.py``) imports and starts these in-process; this entrypoint is
for running the mocks standalone (e.g. local dev, or the CI web job booting them
in the background before the host).
"""

from __future__ import annotations

import argparse
import asyncio
import contextlib

import uvicorn
from starlette.applications import Starlette

from . import SEARXNG_PORT, VLLM_PORT
from .searxng import app as searxng_app
from .vllm import app as vllm_app


async def _serve(app: Starlette, host: str, port: int) -> None:
    config = uvicorn.Config(app, host=host, port=port, log_level="warning")
    server = uvicorn.Server(config)
    await server.serve()


async def _run(host: str, vllm_port: int, searxng_port: int) -> None:
    await asyncio.gather(
        _serve(vllm_app, host, vllm_port),
        _serve(searxng_app, host, searxng_port),
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Boot the Gert E2E mock upstreams.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--vllm-port", type=int, default=VLLM_PORT)
    parser.add_argument("--searxng-port", type=int, default=SEARXNG_PORT)
    args = parser.parse_args(argv)

    print(
        f"mocks: vLLM on http://{args.host}:{args.vllm_port}  "
        f"SearXNG on http://{args.host}:{args.searxng_port}"
    )
    with contextlib.suppress(KeyboardInterrupt):
        asyncio.run(_run(args.host, args.vllm_port, args.searxng_port))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
