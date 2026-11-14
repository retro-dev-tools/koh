# Koh roadmap

Assessment date: 2026-11-14. Source baseline: `c2fddcf`, with pre-existing uncommitted RGBDS expression changes and a modified JRPG screenshot. This is an initial assessment of the architecture, build and release configuration, representative implementation and tests, documentation, and recent history. It is not a complete code, security, licensing, or hardware conformance audit.

## Product direction

Serve Game Boy hobbyists who want to write assembly or constrained C#, build a ROM, and diagnose problems without stitching together unrelated editor and debugger integrations. The differentiator is the connected development workflow: semantic assembly tooling plus C# compilation, sample games, and an emulator/debugger in one repository.

The next milestone should be one documented, reproducible source-to-ROM-to-debugger workflow. Prefer making that journey reliable over expanding the language, emulator UI framework, or number of command-line tools. Preserve existing file formats and game behavior unless a demonstrated correctness issue requires a change.

## Current condition

- Shared diagnostics, opcodes, and object formats are separated from assembler, linker, compiler, emulator, and UI projects. Recent architecture commits deliberately removed compiler dependencies on assembler implementation details. Retain those boundaries; there is no demonstrated need for another restructure.
- Tests cover individual components and compile and emulate sample games. CI builds on Linux and Windows, runs extension tests, publishes the emulator with NativeAOT on three host platforms, and builds the assembly 2048 ROM. These workflow definitions describe intended checks, not evidence that current CI passes.
- Release automation creates platform archives, checksums, and draft releases. A draft-and-smoke-test gate already exists; improve and verify it rather than replacing it.
- Public documentation underrepresented the C# and emulator workflow. The README and contribution guide now expose those entry points, without claiming complete RGBDS or .NET compatibility.
- The README and extension metadata declare MIT, but no tracked LICENSE or NOTICE file was found in the checkout or matching filename history. This needs an ownership and attribution review, not an invented copyright holder.
- `GbJrpgTests` writes acceptance captures under the test binary's `TestResults/gb-jrpg/<run-id>/`. Its regression guard checks that curated sample screenshots are not rewritten. Updating documentation images is a deliberate manual step.
- The local Koh.Sdk package imports repository-relative source projects. Its presence in a local feed is useful for this checkout but does not establish a standalone SDK distribution story.

## Baseline verification

On .NET SDK 10.0.113, `dotnet msbuild build.proj -t:Test` exited 1 before tests ran. NuGet audit warnings treated as errors reported:

| Package | Reported version | Audit result |
| --- | --- | --- |
| MessagePack | 2.5.302 | NU1902, including [GHSA-qhrr-8q5h-9q3h](https://github.com/advisories/GHSA-qhrr-8q5h-9q3h) |
| Microsoft.DotNet.ILCompiler | 10.0.13 | NU1903, [GHSA-p3jp-9v83-76w9](https://github.com/advisories/GHSA-p3jp-9v83-76w9) |
| SixLabors.ImageSharp | 3.1.12 | NU1902 and NU1903, multiple advisories |

These were restore diagnostics, not independently validated exploitability findings. They describe the initial baseline before the dependency repair below.

Both documented sample builds were also attempted and exited 1: `BuildSample2048` was blocked by the ILCompiler advisory, and `dotnet build samples/csharp/gb-2048-v2/Gb2048V2.csproj` was blocked by ILCompiler and ImageSharp advisories. Neither run established a newly built ROM. Documentation checks passed for nine local links and balanced code fences across the README, contribution guide, and this roadmap.

## Priorities

Order reflects impact, effort, risk, and dependencies; it is not a release-date commitment.

Dependency repair: SDK 10.0.115 and StreamJsonRpc 2.26.150 resolve to ILCompiler 10.0.15 and MessagePack 2.5.307. ImageSharp 3.2.0 cleared the reported advisories but required a license key for release builds. Its sole use was replaced with framebuffer PNG output using .NET compression. Screenshot formats other than PNG are now rejected explicitly.

## Completed milestones

- `c965bf9`: documented both game-development entry points and contributor checks.
- `c755769`, dependency repair: restore and CI release build pass with auditing enabled (0 warnings/errors); the fast suite passes 1,673/1,673. The transitive vulnerability report covers 41 projects with no reported vulnerable packages or audit problems. The assembly 2048 sample builds; its headless capture shows the title screen, and independent PNG checks validate dimensions, compression, and every chunk CRC. Rejected JPEG output preserves an existing file. The CI build also emits all seven C# sample ROMs.
- `2451c6c`, screenshot isolation: the new guard fails against the old writer in an isolated worktree and passes after redirecting captures. All four source screenshot SHA-256 hashes are preserved, including the pre-existing modified battle image. All four generated 480x432 captures have valid PNG chunk CRCs. The full CI suite passes 2,485/2,485 with one test/module at a time (5m44s). The compiler test process still consumes several GB; serialization reduces concurrency but does not establish a low-memory workflow.
- Portable emulator targets: publish defaults to the host RID; launch selects the correct executable name. Dispatch checks pass for Linux, macOS, Windows, and the host default, including paths containing spaces. The root target performs a real Linux x64 NativeAOT publish; its executable runs the assembly sample and produces a 160x144 PNG with valid CRCs and decompressed pixel data. The exact new CI smoke step passes locally on Linux. The normal `build.proj -t:Test` loop also passes 1,673/1,673. Windows/macOS execution and GUI launch remain unverified locally; CI is configured to exercise native headless captures on all three hosts.

- [PR #48](https://github.com/retro-dev-tools/koh/pull/48) review follow-up: the capture guard compares the source filename set and SHA-256 hashes. Temporary mutation probes reject timestamp-preserving rewrites, added files, and removed files; the old timestamp guard accepted the rewrite. All five JRPG tests and all 1,673 fast-suite tests pass on the isolated PR checkout.

## Planned work

| Order | Outcome | Effort / risk | Acceptance criteria |
| --- | --- | --- | --- |
| 1 | Close RGBDS expression regressions | Small / medium | Review the existing uncommitted HIGH/LOW/BANK work separately; compare focused object/link results with a versioned RGBDS oracle, then run the affected suites. |
| 2 | Establish release readiness | Medium / ownership review required | Confirm license text and asset provenance with the rights holder; inventory shipped dependencies; smoke-test draft archives and the extension's install/update path on supported hosts. |
| 3 | Prove the newcomer journey | Medium / low | From a fresh checkout, build both documented games, launch a ROM, set a breakpoint, and inspect state; record platform prerequisites and compatibility limits. |
| 4 | Specify the supported surface | Medium / medium | Publish tested C# subset and RGBDS compatibility limits, backed by fixtures; document whether Koh.Sdk is checkout-only or independently consumable. |
| 5 | Bound compiler-test memory | Medium / medium | Measure retained fixture/compiler state and establish a bounded-memory full-suite invocation before changing parallelism defaults. |

## Boundaries

No adoption, performance, hardware-accuracy, or complete compatibility claims are established by this assessment. Defer new language features, UI expansion, dependency replacements without evidence, and architectural rewrites until the core journey has a reproducible passing baseline. Reassess priorities when that evidence changes.
