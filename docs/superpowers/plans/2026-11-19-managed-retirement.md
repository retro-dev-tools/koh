# Managed-code retirement implementation plan

**Goal:** Keep C# only for the supported .NET source language, SDK/MSBuild glue
and independent .NET execution oracles; retire or port everything else in PR62.
**Scope:** User clarification on 2026-11-19 supersedes the engine design's
verification/development-tool exclusions. Preserve existing verification behavior
and native workflow gates. Execute inline; no merge or release.

- [x] Replace the assembler execution verifier with the native test host;
  exercise valid ROMs, missing signatures and malformed input through tests and RGBDS.
- [x] Port the 2048 verification workflow to the native emulator; preserve state
  assertions, screenshots and animation/contact-sheet artifacts.
- [x] Decommission managed bridge/emulator benchmarks and the experimental
  superoptimizer, recording their disposition and removing their build/CI entry points.
  Keep native compiler measurement workloads and their required C# input fixtures.
- [x] Audit managed test coverage against native suites; port missing behavioral
  cases, then retire the managed tests and compatibility libraries.
- [x] Remove C# emulator/boot/verification, opcode/object/common and native bridge
  source once callers are gone; preserve boot assembly and native build glue.
- [x] Update solutions, build targets, CI tasks, package versions and current docs.
  List every retained C# file category and its required .NET consumer.
- [x] Move tile-sheet conversion/PNG decoding to Rust; retain only its MSBuild wrapper.
- [x] Move 2048 image presentation to Rust; keep Python only as an optional launcher/test tool.
- [ ] Run native suites, independent RGBDS verification, source-to-ROM builds,
  packaged editor/desktop checks and final PR CI. Re-read completed logs.

Commit each working increment. The draft remains incomplete until every
retirement and verification item above has authoritative evidence.
