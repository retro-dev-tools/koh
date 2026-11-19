# Contributing to Koh

Start with a reproducible bug, a documentation correction, or a focused improvement to an existing workflow. Discuss substantial changes in an issue before implementing them. Keep pull requests limited to one behavior or problem.

## Development setup

Install the .NET 10 SDK selected by `global.json`. From the repository root:

```sh
dotnet restore Koh.Ci.slnf
dotnet build Koh.Ci.slnf --no-restore --configuration Release
dotnet msbuild build.proj -t:Test
```

Restore also installs the local tools and Husky.NET pre-commit hook. The hook formats staged C# with the pinned CSharpier version and stages the formatted files again. Review the staged diff before committing.

NuGet audit warnings are errors. If restore reports a vulnerable package, include the package version and advisory URL in your report. A failed restore means the tests have not run; do not report it as a test failure or bypass auditing to claim a green build.

## Choosing checks

Run focused native checks first. For example, for assembler changes:

```sh
cargo nextest run -p koh-asm --locked
dotnet msbuild build.proj -t:Test
```

Compiler tests are Rust targets, including Roslyn-built DLL/PDB fixtures and
external execution in the existing C# emulator. Install cargo-nextest and run:

```sh
cargo nextest run --workspace --all-features --locked
cargo test --workspace --all-features --doc --locked
```

The committed nextest configuration limits Roslyn/emulator concurrency and uses
zero retries. `just ci` runs full acceptance on Linux, Windows and macOS in CI.
Remaining .NET tests belong to live emulator, debugger, LSP, object/SDK bridge
and standalone tool components. `build.proj -t:TestAll` runs both test ecosystems.

Desktop publication requires Rust 1.92+, Python 3.11+ and the [GPUI native dependencies](docs/gpui-migration.md).
`dotnet msbuild build.proj -t:PublishEmulatorApp` publishes the Rust UI and
windowless engine together. On Linux, `scripts/verify-gpui.py` exercises that
package in its own Xvfb server using the SDK 2048-v2 ROM.

For VS Code changes, install Node.js and npm, then:

```sh
cd editors/vscode
npm ci
npm test
```

On headless Linux, use `xvfb-run -a npm test` as CI does. To debug the extension, run `dotnet msbuild build.proj -t:PublishDev` from the repository root, open `editors/vscode` in VS Code, and press F5.

Check C# formatting with `dotnet csharpier check <changed-file-or-directory>`. Avoid formatting unrelated files. Do not run builds concurrently in the same output directory.

## Regression coverage

Place tests under the matching area in `tests/`. A behavior fix should include a focused regression that fails before the fix. For RGBDS compatibility, include the smallest assembly reproducer and the reference tool's version and output. For emulator fixes, distinguish automated emulator results from tests on physical hardware.

Do not commit downloaded ROMs, toolchains, generated build output, VSIX files, or test caches. JRPG acceptance captures go under the test binary directory at `TestResults/gb-jrpg/<run-id>/`. The sample's tracked screenshots are curated documentation assets; copy reviewed captures there only when deliberately updating the documentation. Check `git status` after tests and preserve pre-existing edits.

## Reporting and reviewing changes

Bug reports should include the commit or release, OS, relevant tool versions, command, expected result, actual result, and a minimal reproducer. Remove secrets and machine-specific paths from logs.

Pull requests should explain the user-visible problem and resulting behavior, list the commands actually run with their results, and identify checks that were blocked or skipped. Include screenshots or recordings for editor and debugger UI changes. Use scoped Conventional Commit subjects, such as `fix(link): handle ...` or `docs: explain ...`.

The README declares MIT, but this checkout has no tracked license text. Copyright ownership and third-party attribution need maintainer review before release; do not add license claims for code or assets whose provenance is unknown.
