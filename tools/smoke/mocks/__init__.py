"""Mock upstreams for the Gert E2E harness (testing.md §4.2).

The real ``Gert.External`` adapters point here in the FakeE2E profile, so the
adapter HTTP code (IHttpClientFactory/Polly, OpenAI request shaping, SSE parsing,
the SSRF guard) is exercised against wire-level fakes. All driven by the shared
deterministic spec in :mod:`tools.smoke.mocks.specs`.
"""

# Default localhost ports the FakeE2E launch profile expects.
VLLM_PORT = 8000
SEARXNG_PORT = 8080
