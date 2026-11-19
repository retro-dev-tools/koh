# CI validation

`CI` has one sequential `check` job and one platform matrix.

| Event | Platforms |
| --- | --- |
| Ordinary PR | Linux |
| Push to `master` | Linux, Windows, macOS |
| Manual run | Linux, Windows, macOS |

Every selected platform checks Rust formatting/lints, workspace tests and docs,
remaining .NET projects, release compiler/SDK execution, language-server
publication, native desktop/engine packaging, audio resets, headless captures,
native-window behavior and the real VS Code debugger path. Formatting runs on
Linux; test/build coverage runs on every selected platform.

Linux additionally compares pinned RGBDS/ELF pipelines and checks
benchmark discovery. Existing verification tools are unchanged. Tag-only
toolchain and extension releases remain separate workflows.

There is no changed-path classifier or aggregate pass job. Each check reports
its own actual result. New runs supersede older runs of the same mode on the
same branch. PR checks cannot cancel master/manual validation. A
shared per-platform cache avoids repeated dependency downloads/builds; GitHub
keeps PR caches scoped to their PR refs. Diagnostic captures, packages and
release tools are retained in `validation-<OS>` artifacts for seven days.

Trigger full validation for a branch with:

```sh
gh workflow run ci.yml --ref <branch>
```

Windows excludes the two Unix-specific assembler filesystem cases,
`include_provenance_preserves_the_logical_symlink_path` and
`physical_include_keys_preserve_native_path_separators`. They are platform
exclusions, not retried or ignored failures. Source-debugger tests run on all
three platforms. The explicit Unix socket-path case is also Unix-only;
Windows exercises named pipes instead.

## Required-check transition

Master protection currently requires seven legacy job names, including three
`emulator-aot-publish` contexts. Those names do not exist in the simplified
workflow. No branch-protection settings are changed before the coordinated merge.

When merging this CI change, replace the legacy contexts with
`check (ubuntu-latest)` using the prepared payload. It retains strict protection
and the GitHub Actions app binding. Coordinate this change with the merge so
other PRs are not left waiting for a check their workflow cannot emit:

```sh
gh api --method PATCH repos/retro-dev-tools/koh/branches/master/protection/required_status_checks --input .github/required-checks.json
gh api repos/retro-dev-tools/koh/branches/master/protection/required_status_checks
```

Confirm the response contains only `check (ubuntu-latest)`, app `15368`, and
`strict: true`. The additional Windows/macOS checks run on master/manual
validation; the user selected Linux-only automatic PR validation.
