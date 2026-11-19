# Build, test and release

Install stable Rust (rustfmt and clippy), cargo-nextest 0.9.140, .NET 10.0.115
or a compatible newer .NET 10 SDK, Node 22, Python 3.11+ and
[just 1.59+](https://just.systems/man/en/).
Python must be available as `python`; the task file uses its standard library
for platform handling, without Bash or PowerShell on Windows.

Run `just` to list tasks. Tasks with arguments: `just run [ROM/options...]`,
`just package tools-v0.1.6-rc.1`, `just extension ext-v0.1.5`.

MSBuild retains SDK integration, publishing and focused .NET targets.
Cargo and npm remain usable directly for focused development.
No package task publishes anything. Versions are passed as arguments, never
interpolated into executable code. Toolchain bundle versions are independent
of crate versions; extension tags must match both npm manifests.

Full acceptance also needs Pillow 13.0.0 (`python -m pip install Pillow==13.0.0`).
Linux needs the native packages listed in `ci.yml`, Xvfb, ShellCheck and
actionlint 1.7.12 (`go install github.com/rhysd/actionlint/cmd/actionlint@v1.7.12`).
Put Go's bin directory on PATH. RGBDS 1.0.1 is built under artifacts for the
independent assembler/linker comparisons. Windows needs MSVC build tools;
Windows packaging also needs Inno Setup 6. macOS packages use the host architecture.

CI runs the same full Linux, Windows and macOS tasks for every PR, master push
and manual run. Named lint, build/test and integration steps isolate failures;
platform-independent lint runs early, once on Linux. `just ci` selects the same
phases for the host; `just verify` uses the outputs of `just build`/`just test`.
MSRV covers core libraries/binaries; UI and test-host require
newer Rust and run on stable. Nextest retains zero retries and bounded Roslyn
concurrency. The tests include packaged LSP stdio, source-to-ROM execution,
native audio resets, framebuffer PNGs, native windows and actual VS Code
debugging. Linux also checks workflow/shell syntax, RGBDS parity and benchmark
discovery. NuGet auditing, warnings-as-errors and npm production auditing remain
enabled. Results and diagnostic captures are uploaded for seven days.

## Releases

Tag pushes (`tools-v*` or `ext-v*`) package artifacts and create **drafts only**.
Manual release workflows take a version and upload packages without creating a
release. Run full CI on the same commit first. Download and test the packages;
platform manifests contain SHA-256 checksums. The draft job combines the
toolchain manifests and verifies every checksum before attaching assets.
Archives include native dependency notices and source/toolchain build information.

Publication remains blocked by the license/attribution review in ROADMAP.md.
After review, a maintainer can download a draft with `gh release download <tag>`,
verify `sha256sum -c SHA256SUMS.txt` (macOS: `shasum -a 256 -c SHA256SUMS.txt`),
test the actual installers/editor update path, and deliberately publish using
`gh release edit <tag> --draft=false`. Automation never publishes a public release.

PR jobs use read-only tokens and no persisted checkout credentials. Only the
draft job receives contents:write. Action refs remain pinned and Dependabot
maintains them. Repository protection is managed separately: the intended
required contexts are `check (ubuntu-latest)`, `check (windows-latest)`,
`check (macos-latest)` and `msrv`. All four run on every PR without path filters
or conditional job skips. An administrator must replace the seven obsolete
contexts with these four, bind each to GitHub Actions (`app_id: 15368`), and retain
`strict: true` (require an up-to-date branch) before merging. This refactoring
changes no repository rules automatically.
