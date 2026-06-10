# Gert — developer Makefile
# Quoted goals: `make test` and `make run` (mocked env), plus coverage.
# .NET 10 + uv (Python) toolchain. See docs/design/.

SLN          := Gert.sln
CONFIG       ?= Debug
COVERAGE_DIR := artifacts/coverage
SMOKE_DIR    := tools/smoke

.DEFAULT_GOAL := help

.PHONY: help
help: ## Show this help
	@grep -hE '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) \
		| awk 'BEGIN{FS=":.*?## "}{printf "  \033[36m%-12s\033[0m %s\n", $$1, $$2}'

.PHONY: restore
restore: ## Restore NuGet packages
	dotnet restore $(SLN)

.PHONY: build
build: ## Build the solution (warnings are errors)
	dotnet build $(SLN) -c $(CONFIG)

.PHONY: test
test: ## Run the full .NET test suite
	dotnet test $(SLN) -c $(CONFIG) --nologo

.PHONY: lint
lint: ## Enforce ruff (lint + format check) + mypy --strict on the Python harness
	cd $(SMOKE_DIR) && uv run ruff check . && uv run ruff format --check . && uv run mypy .

.PHONY: lint-fix
lint-fix: ## Auto-fix ruff lint + format on the Python harness
	cd $(SMOKE_DIR) && uv run ruff check --fix . && uv run ruff format .

.PHONY: check-links
check-links: ## Verify every relative link/anchor in tracked markdown resolves (CI gate)
	python3 tools/check_links.py

.PHONY: smoke-unit
smoke-unit: ## Run the non-browser Python checks (embedding conformance) — no Playwright needed
	PYTHONPATH=. $(SMOKE_DIR)/.venv/bin/python -m pytest $(SMOKE_DIR)/tests/test_embeddings_conformance.py -q

.PHONY: smoke-auth
smoke-auth: ## Boot mocks + FakeE2E host and run the API auth smoke (httpx only, no browsers)
	cd $(SMOKE_DIR) && uv sync
	PYTHONPATH=. $(SMOKE_DIR)/.venv/bin/python -m tools.smoke.run --api-smoke

.PHONY: serve-mock
serve-mock: ## Boot python mocks + FakeE2E host + a dev proxy; open the printed URL in YOUR browser (no Playwright). ROLE=admin|user|limited
	cd $(SMOKE_DIR) && uv sync --group monty
	PYTHONPATH=. $(SMOKE_DIR)/.venv/bin/python -m tools.smoke.run --proxy --monty-real --role $(or $(ROLE),admin)

.PHONY: serve-mock-vllm
serve-mock-vllm: ## serve-mock, but chat hits a REAL vLLM at VLLM_URL; optional SEARXNG_URL makes web search real too (e.g. https://searx.zaggy.nl — needs format=json). Auth stays mocked. [VLLM_MODEL=...] [ROLE=admin]
	@test -n "$(VLLM_URL)" || { echo "usage: make serve-mock-vllm VLLM_URL=http://<host>:8000/v1 [SEARXNG_URL=https://searx.zaggy.nl] [VLLM_MODEL=<id>] [ROLE=admin|user|limited]"; exit 2; }
	cd $(SMOKE_DIR) && uv sync --group monty
	PYTHONPATH=. $(SMOKE_DIR)/.venv/bin/python -m tools.smoke.run --proxy --monty-real --role $(or $(ROLE),admin) \
		--vllm-url $(VLLM_URL) $(if $(VLLM_MODEL),--vllm-model $(VLLM_MODEL)) \
		$(if $(SEARXNG_URL),--searxng-url $(SEARXNG_URL))

.PHONY: coverage
coverage: ## Run tests with coverage + generate an HTML report (needs coverlet.collector + reportgenerator tool)
	rm -rf $(COVERAGE_DIR)
	dotnet test $(SLN) -c $(CONFIG) --nologo \
		--collect:"XPlat Code Coverage" --results-directory $(COVERAGE_DIR)
	dotnet tool run reportgenerator \
		-reports:"$(COVERAGE_DIR)/**/coverage.cobertura.xml" \
		-targetdir:"$(COVERAGE_DIR)/report" \
		-reporttypes:"Html;TextSummary"
	@echo "── coverage summary ──" && cat $(COVERAGE_DIR)/report/Summary.txt 2>/dev/null || true
	@echo "HTML report: $(COVERAGE_DIR)/report/index.html"

.PHONY: run
run: ## Run the API host (real adapters arrive in U10; chat needs the mocked env via `make dev`)
	dotnet run --project src/Gert.Api -c $(CONFIG)

.PHONY: dev
dev: ## Run the host against the MOCKED world: Python mock upstreams + FakeE2E profile (requires U13)
	@if [ -d "$(SMOKE_DIR)/mocks" ]; then \
		echo "Booting python mock upstreams + FakeE2E host…"; \
		uv run --directory $(SMOKE_DIR) python -m mocks & \
		dotnet run --project src/Gert.Api --launch-profile FakeE2E -c $(CONFIG); \
	else \
		echo "make dev needs tools/smoke/mocks (U13) — not built yet."; exit 1; \
	fi

.PHONY: e2e
e2e: ## Run the Python + Playwright E2E matrix against the mocked host (requires U13)
	@if [ -f "$(SMOKE_DIR)/run.py" ]; then \
		uv run --directory $(SMOKE_DIR) python -m run --browser all --role all; \
	else \
		echo "make e2e needs tools/smoke (U13) — not built yet."; exit 1; \
	fi

.PHONY: clean
clean: ## Remove build + coverage artifacts
	dotnet clean $(SLN) -c $(CONFIG) || true
	rm -rf $(COVERAGE_DIR)
	find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true
