"""proxy.py — a tiny dev reverse-proxy so you can view the SPA in your OWN browser.

It sits in front of the FakeE2E host and injects a dev bearer the F2-safe way: a
``<script src="/__dev_token.js">`` (same-origin, so the host's strict CSP
``script-src 'self'`` allows it) sets ``window.GERT_DEV_TOKEN`` before ``app.js``,
which the SPA's dev-only ``ensureSession`` branch consumes. Everything else (CSS, the
ES modules, ``/api``, and the SSE message stream) is proxied through untouched. No
Playwright, no browser launch — boot it and open the printed URL.
"""

from __future__ import annotations

import json
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

import httpx
from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import Response, StreamingResponse
from starlette.routing import Route

# Hop-by-hop headers (never forwarded) + ones we recompute. content-encoding is dropped
# because httpx already decodes the body for us (we forward the decoded bytes).
_DROP = {
    "connection",
    "keep-alive",
    "transfer-encoding",
    "te",
    "trailer",
    "upgrade",
    "proxy-authorization",
    "proxy-authenticate",
    "host",
    "content-length",
    "content-encoding",
}

_PROXY_METHODS = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"]


def make_proxy_app(upstream: str, token: str) -> Starlette:
    """Build the proxy ASGI app forwarding to ``upstream`` with ``token`` injected."""
    client = httpx.AsyncClient(base_url=upstream, timeout=None)
    token_js = f"window.GERT_DEV_TOKEN={json.dumps(token)};\n".encode()
    inject = b'<script src="/__dev_token.js"></script>\n    '

    async def dev_token(_: Request) -> Response:
        return Response(token_js, media_type="text/javascript")

    async def proxy(request: Request) -> Response:
        headers = {k: v for k, v in request.headers.items() if k.lower() not in _DROP}
        upstream_req = client.build_request(
            request.method,
            request.url.path,
            params=request.query_params,
            headers=headers,
            content=await request.body(),
        )
        resp = await client.send(upstream_req, stream=True)
        out_headers = {k: v for k, v in resp.headers.items() if k.lower() not in _DROP}
        content_type = resp.headers.get("content-type", "")

        # HTML: buffer + inject the dev-token script just inside <head>.
        if "text/html" in content_type:
            raw = await resp.aread()
            await resp.aclose()
            html = raw.replace(b"<head>", b"<head>\n    " + inject, 1)
            return Response(
                html,
                status_code=resp.status_code,
                headers=out_headers,
                media_type=content_type,
            )

        # Everything else (CSS / JS / API / SSE) streams through, decoded.
        async def body() -> AsyncIterator[bytes]:
            try:
                async for chunk in resp.aiter_bytes():
                    yield chunk
            except httpx.TransportError:
                # Upstream died mid-stream (e.g. the mock was terminated while an
                # SSE stream was open) — end the response instead of blowing up.
                pass
            finally:
                await resp.aclose()

        return StreamingResponse(
            body(),
            status_code=resp.status_code,
            headers=out_headers,
            media_type=content_type,
        )

    # Aborting the harness (Ctrl+C) drives uvicorn's graceful shutdown, which runs
    # the lifespan exit — close the upstream client so its pooled keep-alive
    # connections (timeout=None, so they never expire on their own) don't leak.
    # (This Starlette version dropped the on_startup/on_shutdown kwargs; lifespan
    # is the supported hook.)
    @asynccontextmanager
    async def lifespan(_: Starlette) -> AsyncIterator[None]:
        yield
        await client.aclose()

    return Starlette(
        routes=[
            Route("/__dev_token.js", dev_token),
            Route("/{path:path}", proxy, methods=_PROXY_METHODS),
        ],
        lifespan=lifespan,
    )
