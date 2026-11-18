# Linux packaged native-tool checkpoint

Validated the legacy-removal candidate with a host-native emulator publish and
staged production compiler/linker binaries, not test adapters. This is local
Linux evidence; the final three-platform workflow remains required.

## Publish and execution

`dotnet msbuild build.proj -t:PublishEmulatorApp -p:EmulatorRuntimeIdentifier=linux-x64`
passed, exit0. The completed log `/tmp/koh-removal-emulator-publish.log` records
native code generation and the published output. The NativeAOT executable ships
without `Koh.Emulator.App.dll`.

Staged tools under a temporary directory containing spaces, then built the actual
graphics sample with these SDK overrides:

```sh
dotnet build samples/csharp/gb-gfx-demo/GbGfxDemo.csproj -c Release -m:1 \
  "-p:KohCompilerExecutable=<stage>/bin/koh-compile" \
  "-p:KohLinkExecutable=<stage>/bin/koh-link" \
  "-p:KohObjectPath=<stage>/game.o" "-p:KohRomPath=<stage>/game.gb"
```

The build passed, exit0, in3.84s with zero warnings/errors. Log:
`/tmp/koh-package-sdk-build.log`. The65536-byte ROM, nonempty KDBG and canonical
ELF32 little-endian relocatable object were produced through the staged binaries.
The ELF header uses EM_NONE and flags0x4b4f4801; the Rust linker validates the
Koh ABI note. No legacy compiler source or assembly exists in this checkout.

The staged NativeAOT emulator ran the ROM for1200 frames, exit0, with screenshot
and mode3 reports. DOTNET_ROOT variables were absent from that process. The PNG
loads as160×144 RGBA with four colors; its visible checkerboard, sprites and HUD
provide an artifact for review. The report records zero mode3 writes in tile,
BG and window regions. Logs/artifacts are under `/tmp/koh-package-emulator.log`
and the directory recorded in `/tmp/koh-package-smoke-root.txt`. This smoke does
not replace the native suite's exact graphics, timeline and source-debug checks.

| Executable | Bytes | SHA256 |
| --- | ---: | --- |
| koh-compile |2077584|`c350d7d17ae15b8bcb25dad0de855770045c7a64beec849e1fa637cb81d20706`|
| koh-link |709600|`e24a08b3b061c683496bf8a164be5c57eae6f2a86effe3ec8d00176dce5a3655`|
| Koh.Emulator.App |6835968|`f7175a9fe4a8f437129142216f21bb76925540681876a51f04c94a82b05d81b6`|

These are current deployment sizes, not a claim of improvement over the older
[compiler-core performance checkpoint](compiler-performance.md).

## Archive and dependency notices

Created and extracted a tar.gz archive into another path containing spaces.
All three executable byte hashes and executable modes survived extraction;
extracted compiler/linker version commands returned1.0.2. Every license text and
the inventory matched the checkout. Archive audit passed, exit0:
`/tmp/koh-package-archive-audit.log`. The local10313309-byte archive has SHA256
`1342fcaa81ff4612d9868960927d12953d5d4dd4ca2b8f9fdd9d56061bcb112d`.
Archive metadata is not asserted reproducible byte-for-byte.

The dependency audit accounts for all60 registry packages in Cargo.lock and
verifies all60 cached crate SHA256 values. All61 selected license-text references
match their archive bytes, except relative-path's intentionally absent archive
license, verified against its exact upstream commit instead. Its Git blob is
`39d4bdb5acd313c1a92dbeaa1c379aaf0596a315`, matching the bundled text. Foldhash's
copy now preserves the upstream file's absent final newline. No license wording
or attribution changes. Completed audit: `/tmp/koh-package-license-audit.log`.

This verifies the new native dependency bundle and payload, not every bundled
component's licensing. The repository README/Cargo metadata declares MIT but no
first-party LICENSE file is tracked. First-party/toolchain-wide release notices
remain a release-review item; this checkpoint does not invent a copyright owner
or claim that the Cargo inventory audits Rust's standard library.

## Rust runtime notice staging

The release workflow now calls `scripts/stage-rust-notices.py` after building
the compiler. It stages the active sysroot's complete `COPYRIGHT-library.html`
and accompanying license-text directory under `licenses/rust-runtime`, with
`rustc --version --verbose` provenance. The three-host native CI artifact job
uses the same helper. Missing upstream files fail the staging command rather
than silently shipping an empty notice directory. No license terms are rewritten.

Local checks stage actual Rust1.99.0 Gentoo and minimum Rust1.88.0 notices into
paths containing spaces. Both commands exit0; each stages13 upstream files with
byte-identical SHA256 values, plus compiler provenance. All relative HTML links
resolve (these upstream files currently contain only anchors and external links).
An initial check incorrectly expected local file links; correcting that check
does not change the upstream HTML or staged payload. Both MIT and Apache texts
are present. This validates the helper locally, not a new hosted three-platform
release or the first-party/managed-payload licensing review.

Final hosted validation44330863800 atf5a10e8 also passes notice staging and
native artifact smoke on Linux, Windows and macOS. Downloaded native archives
match their GitHub SHA256 metadata and each contains14 runtime notice/provenance
files identifying Rust1.100.0. See [final acceptance](acceptance.md). No release
is published, and existing first-party/managed notice gaps remain release blockers.
