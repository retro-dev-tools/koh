# Metadata fixture setup

The assembly, value/class-layout and reference tests share immutable DLL images
within one serialized nextest run. Each test receives fresh owned byte buffers;
malformed-metadata mutations cannot modify the cached images or later tests.
The four fixture sets are `Reader`, `StructLayouts`, `ClassLayouts` and `References`.
Debug/Release, x64, wrong-version, facade and framework images retain their
existing build configurations and assertions.

`tests/support/image_cache.rs` reuses a set only when both the nextest run ID and
the `roslyn` group match. That group remains serialized in `.config/nextest.toml`;
keep Roslyn suites serial in a checkout, as required by the existing shared build
outputs. Each fixture keeps one bounded image set under its ignored `obj` tree.
New runs rebuild. Missing files rebuild. The marker is invalidated before building
and published only after all builds, existing managed probes and writes succeed.
Ordinary `cargo test` and runs outside that group always build privately.

Only DLL bytes are cached here. These fixtures expose no PDB/source paths to
their consumers; the dedicated source-debugger tests retain their existing
private build behavior. No tracked fixture source or sample asset is written.
The compiler implementation, supported CIL contract and production dependencies
are unchanged.

Four native cache tests cover fresh buffers after mutation, a new run, failed
builds, incomplete image sets and disabled reuse. All89 assembly/layout/reference
consumer cases plus those four checks passed93/93 in34.993s, exit0
(`/tmp/koh-image-cache-consumers.log`). The log records one build of each fixture
set; all existing malformed-input and constraint assertions remain selected.
The reader case also passed outside nextest in4.45s, rebuilding its assemblies
(`/tmp/koh-image-cache-outside.log`).

The same six warmed reader cases passed in26.890s before and4.508s after on this
Linux host, about83% less test time. Their three Roslyn configurations build once
per run instead of once per test process. Logs:
`/tmp/koh-image-cache-before.log` and `/tmp/koh-image-cache-after.log`.
This measures selected regression setup, not compiler throughput or peak memory.

The proposed fast CI selection passed1270/1270 in414.482s before this change.
After reuse it passed all1270 existing cases plus four new cache checks in111.792s
(`/tmp/koh-ci-fast-cached.log`). That is a regression-loop observation with four
added checks; it is not a full-workspace speedup or the required C#-versus-Rust
compiler benchmark. No test expectation, timeout, retry or concurrency limit
was weakened.
