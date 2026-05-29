# ARCP F# SDK developer tasks.

.PHONY: docs-api

docs-api:
	@python3 scripts/gen-api-docs.py
