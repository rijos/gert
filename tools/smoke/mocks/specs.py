"""specs.py — the shared deterministic fake spec, Python side (testing.md App. A).

This module is the Python half of the cross-language anti-drift contract:

* ``embed(text)`` reproduces the A.2 algorithm **byte-for-byte** with the .NET
  ``FakeEmbeddings.Embed`` (SHA256-seeded, big-endian, double arithmetic, float32
  cast only at the very end). The committed ``embeddings_golden.json`` keeps both
  honest — ``tests/test_embeddings_conformance.py`` asserts equality and runs
  WITHOUT a browser.
* The canned completions + web-search results are **read from**
  ``tests/shared/fixtures.json`` — the same file the .NET fakes embed. No second
  copy (A.5 ownership).

Pure: no network, no framework. The mock servers (``vllm.py`` / ``searxng.py``)
import this so a behaviour proven in a unit test is the behaviour the browser sees.
"""

from __future__ import annotations

import hashlib
import json
import math
import struct
from pathlib import Path
from typing import Any

DIMENSIONS = 1024

# Repo root is three levels up: tools/smoke/mocks/specs.py -> repo.
REPO_ROOT = Path(__file__).resolve().parents[3]
SHARED_DIR = REPO_ROOT / "tests" / "shared"
FIXTURES_PATH = SHARED_DIR / "fixtures.json"
GOLDEN_PATH = SHARED_DIR / "embeddings_golden.json"


# --- A.2 deterministic embedding (byte-identical to FakeEmbeddings.Embed) -----
def embed(text: str) -> list[float]:
    """Map ``text`` to a stable 1024-dim L2-unit vector (testing.md A.2).

    ::

        data = utf8(text)
        for i in 0..1023:
            h    = SHA256( data ++ uint32_be(i) )   # index suffix big-endian
            u    = uint32_be( h[0:4] )              # first 4 bytes big-endian
            x[i] = (u / 2**32) * 2 - 1              # double in [-1, 1)
        norm = sqrt(sum x[i]^2)                     # L2 norm, in double
        return [ float32(x[i] / norm) for i ]       # cast to float32 only at the end

    All arithmetic is IEEE-754 double (Python ``float``); the cast to float32 is
    done once, at the end, via ``struct`` round-trip — exactly mirroring the C#
    ``(float)`` cast so the two agree to the bit. ``norm`` is never zero in
    practice; if it were, the canonical basis vector e0 is returned.
    """
    data = text.encode("utf-8")

    x = [0.0] * DIMENSIONS
    for i in range(DIMENSIONS):
        h = hashlib.sha256(data + struct.pack(">I", i)).digest()
        u = struct.unpack(">I", h[0:4])[0]
        x[i] = (u / 4294967296.0) * 2.0 - 1.0

    norm = math.sqrt(sum(v * v for v in x))

    if norm == 0.0:
        result = [0.0] * DIMENSIONS
        result[0] = 1.0
        return result

    # Cast to float32 only at the very end (one round-trip through IEEE-754 single).
    return [_to_float32(v / norm) for v in x]


def _to_float32(value: float) -> float:
    """Round a Python double to float32 precision (mirrors the C# (float) cast)."""
    return float(struct.unpack("<f", struct.pack("<f", value))[0])


# --- fixtures (canned completions + search), read from the shared file --------
_fixtures_cache: dict[str, Any] | None = None


def fixtures() -> dict[str, Any]:
    """Load and cache ``tests/shared/fixtures.json`` (the single source of truth)."""
    global _fixtures_cache
    if _fixtures_cache is None:
        _fixtures_cache = json.loads(FIXTURES_PATH.read_text(encoding="utf-8"))
    return _fixtures_cache


def golden() -> dict[str, Any]:
    """Load the committed embeddings golden file (for the conformance test)."""
    data: dict[str, Any] = json.loads(GOLDEN_PATH.read_text(encoding="utf-8"))
    return data


# --- A.3 completion resolution ------------------------------------------------
def _normalize(text: str) -> str:
    return (text or "").strip()


def resolve_completion(last_user_message: str, after_tool: bool) -> dict[str, Any]:
    """Resolve a completion fixture by the last user message (A.3).

    Returns a normalized dict::

        { "deltas": [...], "finish": "stop"|"tool_calls",
          "tool_call": {name, arguments} | None,
          "usage": {"completion_tokens": n} }

    ``after_tool`` selects the follow-up reply (the second model call, whose
    messages now carry the tool result) for a tool-exercising fixture.
    """
    msg = _normalize(last_user_message)
    fx = fixtures()

    for entry in fx.get("completions", []):
        when = _normalize(entry.get("when", ""))
        match = entry.get("match", "exact")
        hit = (msg == when) if match == "exact" else (when.lower() in msg.lower())
        if not hit:
            continue

        # Tool-exercising fixture: first call emits the tool call; the follow-up
        # call replays the same `when` and plays after_tool.deltas.
        if "tool_call" in entry and not after_tool:
            return {
                "deltas": [],
                "finish": entry.get("finish", "tool_calls"),
                "tool_call": entry["tool_call"],
                "usage": entry.get("usage", {"completion_tokens": 0}),
            }

        source = (
            entry.get("after_tool", entry)
            if after_tool and "after_tool" in entry
            else entry
        )
        return {
            "deltas": list(source.get("deltas", [])),
            "finish": source.get("finish", "stop"),
            "tool_call": None,
            "usage": source.get("usage", {"completion_tokens": 0}),
        }

    # Fallback: "echo" — stream "Echo: <message>" tokenised on word boundaries
    # (spaces preserved) so the typewriter/SSE framing has real chunks.
    if fx.get("fallback") == "echo":
        echoed = f"Echo: {msg}"
        deltas = _tokenize_words(echoed)
        return {
            "deltas": deltas,
            "finish": "stop",
            "tool_call": None,
            "usage": {"completion_tokens": len(deltas)},
        }

    return {
        "deltas": [""],
        "finish": "stop",
        "tool_call": None,
        "usage": {"completion_tokens": 0},
    }


def _tokenize_words(text: str) -> list[str]:
    """Split into word chunks with the trailing space preserved on each chunk."""
    chunks: list[str] = []
    current = ""
    for ch in text:
        current += ch
        if ch == " ":
            chunks.append(current)
            current = ""
    if current:
        chunks.append(current)
    return chunks or [text]


# --- A.4 search resolution ----------------------------------------------------
def resolve_search(query: str) -> dict[str, Any]:
    """Resolve a SearXNG result set by query (A.4), substring-matched.

    Returns a SearXNG-shaped dict: ``{"results": [...]}``. The adversarial
    "internal metadata" fixture (a link-local URL) is returned for any query
    mentioning metadata / internal, so the SSRF scenario can trigger it.
    """
    fx = fixtures()
    search = fx.get("search", {})
    q = _normalize(query).lower()

    # Exact key first, then substring either direction.
    for key, value in search.items():
        if q == key.lower():
            return {"results": value.get("results", [])}
    for key, value in search.items():
        if key.lower() in q or q in key.lower():
            return {"results": value.get("results", [])}

    return {"results": []}
