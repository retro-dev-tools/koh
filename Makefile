# Run local tests:           make test
# Run RGBDS compat tests:    make compat-tests  (requires Docker)

.PHONY: test compat-tests

test:
	dotnet test

compat-tests:
	docker compose run --rm --build compat-tests
