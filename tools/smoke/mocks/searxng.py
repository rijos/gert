"""searxng.py - SearXNG JSON search mock (testing.md section 4.2 / A.4 / security F5).

A Starlette ASGI app the *real* ``Gert.External`` SearXNG adapter points at in the
FakeE2E profile. One endpoint:

* ``GET /search?format=json&q=<query>`` - returns ``{"results": [...]}`` resolved
  from the shared fixtures by query.

At least one fixture ("internal metadata") is **adversarial by design**: its
second result is a link-local metadata URL (``http://169.254.169.254/...``). The
real adapter's fetch/summarize step MUST refuse it - that proves the SSRF guard
end-to-end (security F5). The mock just serves the URL; the guard lives in .NET.
"""

from __future__ import annotations

from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import JSONResponse
from starlette.routing import Route

from . import specs


async def search(request: Request) -> JSONResponse:
    query = request.query_params.get("q", "")
    result = specs.resolve_search(query)
    return JSONResponse(
        {
            "query": query,
            "number_of_results": len(result["results"]),
            "results": result["results"],
        }
    )


def create_app() -> Starlette:
    return Starlette(routes=[Route("/search", search, methods=["GET"])])


app = create_app()
