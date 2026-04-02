# Run local tests:           make test
# Run RGBDS compat tests:    make compat-tests  (requires Docker)
# Run benchmarks:            make benchmark     (requires Docker)

.PHONY: test compat-tests benchmark

test:
	dotnet test

compat-tests:
	dotnet test --project tests/Koh.Compat.Tests/

benchmark:
	bash benchmarks/benchmark.sh
