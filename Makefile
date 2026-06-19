# Gert - developer Makefile
# Quoted goals: `make test` and `make run` (mocked env), plus coverage.
# .NET 10 + uv (Python) toolchain. See docs/design/.

SLN          := Gert.sln
CONFIG       ?= Debug
COVERAGE_DIR := artifacts/coverage
SMOKE_DIR    := tools/smoke
HANG_TIMEOUT ?= 180s

# The SPA source tree and the no-npm web toolchain driver (esbuild transpile/bundle + tsgo
# checker; typescript-migration.md). WEBROOT_BUILD is the served dev mirror (gitignored).
WEBROOT       := src/Gert.Api/wwwroot
WEBROOT_BUILD := artifacts/webroot
WEB_TOOL      := dotnet run --project tools/Gert.Web.Bundle --no-launch-profile -c $(CONFIG) --

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
test: ## Run the .NET test suite (excludes the timing-coupled Category=Race set - see test-race)
	dotnet test $(SLN) -c $(CONFIG) --nologo --filter "Category!=Race" --blame-hang-timeout $(HANG_TIMEOUT)

.PHONY: test-race
test-race: ## Race/dead-zone integration tests (paced turns, mid-stream switching) - on demand, NOT part of CI
	timeout $(HANG_TIMEOUT) dotnet run --project tests/Gert.Api.Tests -c $(CONFIG) -- -explicit only -trait "Category=Race"

.PHONY: typecheck
typecheck: ## Type-check the SPA + the tools/markdown harness with tsgo (TS7 native Go checker, no npm) - fail-closed gate
	$(WEB_TOOL) --typecheck $(WEBROOT)
	$(WEB_TOOL) --typecheck tools/markdown

.PHONY: transpile
transpile: ## Build the dev SPA mirror (esbuild .ts -> .js, linked maps) into $(WEBROOT_BUILD)
	$(WEB_TOOL) --transpile $(WEBROOT) $(WEBROOT_BUILD)

.PHONY: md-test
md-test: ## Quick markdown-renderer gate: corpus regression + harness self-test vs the transpiled renderer (no fuzz). Needs node.
	$(WEB_TOOL) --transpile $(WEBROOT) $(WEBROOT_BUILD)
	GERT_WWWROOT=$(CURDIR)/$(WEBROOT_BUILD) node tools/markdown/check.ts
	GERT_WWWROOT=$(CURDIR)/$(WEBROOT_BUILD) node tools/markdown/selftest.ts

.PHONY: md-fuzz
md-fuzz: ## On-demand markdown fuzz + super-linear/ReDoS complexity probe (NOT a CI gate). FUZZ_SECS=20 default. Needs node.
	$(WEB_TOOL) --transpile $(WEBROOT) $(WEBROOT_BUILD)
	GERT_WWWROOT=$(CURDIR)/$(WEBROOT_BUILD) node tools/markdown/complexity.ts
	GERT_WWWROOT=$(CURDIR)/$(WEBROOT_BUILD) node tools/markdown/fuzz.ts --time $(or $(FUZZ_SECS),20)

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
smoke-unit: ## Run the non-browser Python checks (embedding conformance) - no Playwright needed
	PYTHONPATH=. $(SMOKE_DIR)/.venv/bin/python -m pytest $(SMOKE_DIR)/tests/test_embeddings_conformance.py -q

.PHONY: smoke-auth
smoke-auth: ## Boot mocks + FakeE2E host and run the API auth smoke (httpx only, no browsers)
	cd $(SMOKE_DIR) && uv sync
	PYTHONPATH=. $(SMOKE_DIR)/.venv/bin/python -m tools.smoke.run --api-smoke

.PHONY: serve-mock
serve-mock: ## Boot python mocks + FakeE2E host + a dev proxy; open the printed URL in YOUR browser (no Playwright). ROLE=admin|user|limited; default serves the esbuild-transpiled .ts mirror, MINIFY=1 serves the release bundle
	cd $(SMOKE_DIR) && uv sync --group monty
	PYTHONPATH=. $(SMOKE_DIR)/.venv/bin/python -m tools.smoke.run --proxy --monty-real --role $(or $(ROLE),admin) $(if $(MINIFY),--minify,)

.PHONY: coverage
coverage: ## Run tests with coverage + generate an HTML report (needs coverlet.collector + reportgenerator tool)
	rm -rf $(COVERAGE_DIR)
	dotnet test $(SLN) -c $(CONFIG) --nologo \
		--collect:"XPlat Code Coverage" --results-directory $(COVERAGE_DIR)
	dotnet tool run reportgenerator \
		-reports:"$(COVERAGE_DIR)/**/coverage.cobertura.xml" \
		-targetdir:"$(COVERAGE_DIR)/report" \
		-reporttypes:"Html;TextSummary"
	@echo "-- coverage summary --" && cat $(COVERAGE_DIR)/report/Summary.txt 2>/dev/null || true
	@echo "HTML report: $(COVERAGE_DIR)/report/index.html"

.PHONY: run
run: ## Run the API host (chat needs real upstreams) serving the transpiled SPA mirror (esbuild --watch hot-reloads .ts)
	$(WEB_TOOL) --transpile $(WEBROOT) $(WEBROOT_BUILD)
	@$(WEB_TOOL) --watch $(WEBROOT) $(WEBROOT_BUILD) & WATCH_PID=$$!; \
		trap "kill $$WATCH_PID 2>/dev/null || true" EXIT INT TERM; \
		ASPNETCORE_WEBROOT=$(CURDIR)/$(WEBROOT_BUILD) \
		ASPNETCORE_STATICWEBASSETS=$(CURDIR)/$(WEBROOT_BUILD)/.no-swa-manifest \
		dotnet run --project src/Gert.Api -c $(CONFIG)

.PHONY: dev
dev: ## Run the host against the MOCKED world (Python mocks + FakeE2E) serving the transpiled SPA mirror
	# Build the mirror on its OWN recipe line so a transpile failure aborts `make dev` before
	# anything boots (fail-closed): make checks this line's exit status before the next.
	$(WEB_TOOL) --transpile $(WEBROOT) $(WEBROOT_BUILD)
	@if [ -d "$(SMOKE_DIR)/mocks" ]; then \
		echo "Booting python mock upstreams + FakeE2E host (esbuild --watch + transpiled mirror)..."; \
		uv run --directory $(SMOKE_DIR) python -m mocks & MOCKS_PID=$$!; \
		$(WEB_TOOL) --watch $(WEBROOT) $(WEBROOT_BUILD) & WATCH_PID=$$!; \
		trap "kill $$MOCKS_PID $$WATCH_PID 2>/dev/null || true" EXIT INT TERM; \
		ASPNETCORE_WEBROOT=$(CURDIR)/$(WEBROOT_BUILD) \
		ASPNETCORE_STATICWEBASSETS=$(CURDIR)/$(WEBROOT_BUILD)/.no-swa-manifest \
		dotnet run --project src/Gert.Api --launch-profile FakeE2E -c $(CONFIG); \
	else \
		echo "make dev needs tools/smoke/mocks."; exit 1; \
	fi

.PHONY: e2e
e2e: ## Run the Python + Playwright E2E matrix against the mocked host
	@if [ -f "$(SMOKE_DIR)/run.py" ]; then \
		uv run --directory $(SMOKE_DIR) python -m run --browser all --role all; \
	else \
		echo "make e2e needs tools/smoke."; exit 1; \
	fi

.PHONY: clean
clean: ## Remove build + coverage artifacts
	dotnet clean $(SLN) -c $(CONFIG) || true
	rm -rf $(COVERAGE_DIR)
	find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true
