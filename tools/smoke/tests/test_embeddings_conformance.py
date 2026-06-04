"""test_embeddings_conformance.py — the NON-BROWSER anti-drift check (A.2 / A.5).

Asserts ``specs.embed(t)`` reproduces ``tests/shared/embeddings_golden.json`` to
float32 equality. The .NET ``FakeEmbeddingsConformanceTests`` asserts the same
golden, so if either implementation drifts, BOTH go red — the cross-language
contract, made executable.

This test imports no browser fixture and runs anywhere ``uv`` can:

    uv run pytest tools/smoke/tests/test_embeddings_conformance.py
"""

from __future__ import annotations

import struct
from typing import Any

import pytest

from tools.smoke.mocks import specs


def _golden() -> dict[str, Any]:
    return specs.golden()


def test_golden_file_present_and_nonempty() -> None:
    golden = _golden()
    assert golden["dimensions"] == specs.DIMENSIONS
    assert golden["vectors"], "embeddings_golden.json has no vectors"


@pytest.mark.parametrize("text", list(_golden()["vectors"].keys()))
def test_embed_matches_golden_to_float32(text: str) -> None:
    expected = _golden()["vectors"][text]
    actual = specs.embed(text)

    assert len(actual) == specs.DIMENSIONS
    assert len(actual) == len(expected)
    # Bit-for-bit float32 equality. The golden stores shortest-float32 decimals;
    # json parses them to doubles whose tail differs from the float32 value, so we
    # compare the float32 BITS (both sides are the same float32) rather than doubles.
    for i, (e, a) in enumerate(zip(expected, actual, strict=True)):
        assert struct.pack("<f", e) == struct.pack("<f", a), (
            f"dim {i} drift: golden={e} python={a}"
        )


def test_embed_is_unit_length() -> None:
    # L2 norm ~= 1 (float32 rounding aside) — a sanity check on the algorithm.
    import math

    v = specs.embed("qdrant vs sqlite-vec")
    norm = math.sqrt(sum(x * x for x in v))
    assert abs(norm - 1.0) < 1e-4
