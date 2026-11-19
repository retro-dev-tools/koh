# CI/CD guide

Koh uses GitHub Actions and the same Cargo, MSBuild and npm commands used
locally. Tags prepare **drafts**; only a maintainer publishes them. There is no
paid service, automatic merge, or package-registry publication.

## Contributor checks

| Event | Validation |
| --- | --- |
| PR opened, updated, reopened or marked ready | Full Linux check and core Rust MSRV |
| Relevant LSP/editor/package PR changes | Additional focused LSP checks on Linux, Windows and macOS |
| Push to master or manual CI run | Full Linux, Windows and macOS checks and core MSRV |
| tools-v<semver> / ext-v<semver> tag push | Package, smoke-test tools and create a draft |
| Manual release workflow | Packages and checksums only; release job skipped |

The intended mandatory checks are `check (ubuntu-latest)` and `msrv`.
The full check validates workflow syntax/shell snippets, release preflight
tests, Rust/C# formatting, selected native Clippy lints, all native tests and
doctests, .NET builds/tests, compiler/SDK execution, packaged LSP stdio, native
audio resets, headless PNGs, native windows and actual VS Code debugging.
Linux also checks RGBDS 1.0.1/ELF compatibility and benchmark discovery.
Extension production dependencies must pass
`npm audit --omit=dev --audit-level=high`; NuGet auditing and
warnings-as-errors remain enabled.

`msrv` checks production libraries/binaries and all features of the eight core
crates on Rust 1.88.0. Test harnesses run on stable (some use newer standard APIs).
The UI and test-host crates declare Rust 1.92 and remain covered by stable
workspace validation. Focused LSP jobs deliberately overlap Linux CI: they give
quick feedback and Windows/macOS PR coverage without repeating the entire
desktop suite. They are path-filtered and must **not** become required checks.

Superseded validation on the same PR/ref is cancelled. PR numbers isolate
forks with identical branch names; manual runs cannot cancel master pushes.
Release runs queue instead of cancelling packaging. Builds are serial within
each output directory. CI caches dependency downloads and expensive native
builds; GitHub scopes PR caches to their PR refs. Release builds do not restore
those caches. Validation packages, test results and diagnostic screenshots are
retained for seven days in `validation-<OS>` artifacts; focused LSP packages
use `native-lsp-<OS>`.

Windows excludes the two Unix assembler cases
`include_provenance_preserves_the_logical_symlink_path` and
`physical_include_keys_preserve_native_path_separators`. Unix socket-path
tests are Unix-only; Windows exercises named pipes. These are platform
exclusions, not retries. Editor tests use a fresh temporary profile per run.

## Local validation and diagnostics

Prerequisites: stable Rust with rustfmt/clippy, cargo-nextest 0.9.140,
.NET SDK 10.0.115 or compatible newer .NET 10, Node 22, Python 3.13,
actionlint 1.7.12 and ShellCheck. The exact Ubuntu native development packages are in
`ci.yml`; headless editor tests also need Xvfb.

```sh
actionlint
shellcheck installers/unix/koh-install.sh.template
python scripts/test-release-preflight.py
cargo fmt --all -- --check
dotnet tool restore
dotnet csharpier check .
cargo clippy -p koh-emulator -p koh-debugger -p koh-engine -p koh-test-host -p koh-lsp --all-targets --no-deps --locked -- -D warnings
cargo check --workspace --all-targets --all-features --locked
cargo nextest run --workspace --all-features --locked --no-fail-fast
cargo test --doc --workspace --all-features --locked
dotnet build Koh.Ci.slnf -c Release -m:1
dotnet test --solution Koh.Ci.slnf -c Release --no-build --max-parallel-test-modules 1
```

Install Rust 1.88.0 with rustup, then run:

```sh
cargo +1.88.0 check --workspace --exclude koh-ui --exclude koh-test-host --lib --bins --all-features --locked
```

In `editors/vscode`, run `npm ci`,
`npm audit --omit=dev --audit-level=high`, then `npm test`
(`xvfb-run -a npm test` on headless Linux). Two packaged integration cases
are pending without binary/fixture environment variables. See `ci.yml` for
their setup: plain npm test is not packaged desktop/LSP acceptance.
`npm run package` creates a VSIX without publishing.

Run complete platform/package/display validation on a pushed branch:

```sh
gh workflow run ci.yml --ref <branch>
gh run list --workflow ci.yml --branch <branch>
gh run view <run-id> --log-failed
gh run download <run-id> --dir /tmp/koh-validation
```

Read completed logs and test summaries: running/skipped/cancelled is not passing.
Fix formatting with `cargo fmt` or `dotnet csharpier format .` and review
the diff. NU1902/NU1903 and audit failures require dependency repairs, not
suppression. GUI failures have diagnostic captures in validation artifacts.
Delete suspect caches through Actions → Caches, without weakening tests.

## Repository setup

1. Enable Actions on GitHub-hosted runners. Set default workflow tokens to
   read-only and disallow Actions creating/approving PRs. Retain first-time
   fork contributor approval. PRs use pull_request, no secrets, read-only
   tokens and no persisted checkout credentials.
2. Coordinate the required-check transition below with integration. Require
   PR review and an up-to-date branch. Do not require path-filtered LSP jobs
   or platform jobs that only run after merge.
3. Enable Dependabot alerts/security updates. Review weekly grouped Actions
   update PRs: SHAs are pinned and updates still need CI and human review.
   npm/Cargo/NuGet upgrades remain deliberate because toolchain compatibility
   and shipped notice inventories can change.
4. Restrict creation/update/deletion of tools-v* and ext-v* tags to release
   maintainers with repository rulesets. Tags must identify reviewed commits
   with successful full three-platform CI. Never move a released tag.

No repository secrets, PAT, marketplace token or cloud account is needed.
Only draft creation receives contents:write through the temporary GITHUB_TOKEN.
The manual publisher uses the maintainer's own `gh auth login` session.
Nothing uploads to the VS Code Marketplace or NuGet.

The token, action-pinning and untrusted-input choices follow
[GitHub's secure-use guidance](https://docs.github.com/en/actions/reference/security/secure-use).

### Required-check transition

Live inspection on 2026-11-19 confirmed seven obsolete required contexts,
including build-and-test and emulator-aot-publish. These block current PRs.
No remote setting was changed here. After the new jobs report on the integration
PR, a repository admin must coordinate this replacement with the merge:

```sh
gh api --method PATCH repos/retro-dev-tools/koh/branches/master/protection/required_status_checks --input .github/required-checks.json
gh api repos/retro-dev-tools/koh/branches/master/protection/required_status_checks
```

Confirm exactly `check (ubuntu-latest)` and `msrv`, each bound to
GitHub Actions app 15368, with strict:true. Do not remove protection.

## Release procedure

**Public publication remains blocked by first-party license/ownership and
notice review** (ROADMAP.md). The root LICENSE is missing and VSIX packaging
warns about it. A draft does not resolve this. The rights holder must supply
the license and review shipped notices before publication.

1. Choose a reviewed commit with successful full three-platform CI. For an
   extension release, update package.json and package-lock.json together.
   Tags use SemVer; bundle versions are independent of Cargo package versions.
2. Once workflows are on the default branch, test packages on a pushed branch
   without creating a tag or release:

   ```sh
   gh workflow run release-toolchain.yml --ref <branch> -f version=0.1.6-rc.1
   gh workflow run release-extension.yml --ref <branch> -f version=0.1.5
   ```

   Manual runs build the selected ref. Download the run's toolchain-* or vsix
   artifacts and verify the included checksums. Native archives record the
   source SHA and Rust/.NET versions in build-info.txt.
3. Smoke-test extracted archives on Linux x64, Windows x64 and macOS ARM64:
   build/run a C# sample, test the desktop and editor install/debug path.
   Install the VSIX with `code --install-extension <file.vsix>`.
   Installer execution and extension update behavior need maintainer review.
4. Explicitly create and push only the intended tag:

   ```sh
   git tag -a tools-v0.1.6-rc.1 <tested-sha> -m 'Toolchain 0.1.6-rc.1'
   git push origin tools-v0.1.6-rc.1
   ```

   Use ext-v<version> for the extension. The workflow rebuilds, validates,
   packages, smoke-tests tools, checksums and creates a draft with generated
   release notes. Versions beginning 0. and SemVer prereleases are flagged
   prerelease. Windows gets ZIP/Inno Setup; Linux/macOS get tar.gz; Linux also
   supplies koh-install.sh. The extension gets a platform-neutral VSIX.
5. Download the draft with `gh release download <tag> --dir /tmp/koh-release`.
   In that directory, run `sha256sum -c SHA256SUMS.txt` (macOS:
   `shasum -a 256 -c SHA256SUMS.txt`). Review generated notes and test the
   actual draft assets. Close licensing and advisory blockers.
6. Deliberately publish with `scripts/publish-release.sh <tag>` or its
   PowerShell equivalent. Afterwards verify anonymous downloads and the
   extension install/update path.

If packaging fails, fix the cause on a new reviewed commit/version. If a draft
already exists, inspect assets before manually deleting/recreating it: release
creation does not overwrite an existing release. Never publish just to test CI.

Checksums detect corruption, not authenticity. Locked dependencies do not make
moving stable Rust/SDK/runner images or archive timestamps byte-reproducible.
Signing/provenance and frozen release environments are follow-ups after
licensing and real multi-platform dry runs, not claims of this implementation.
