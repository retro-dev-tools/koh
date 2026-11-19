# Rust desktop dependency notices

`manifest.json` records the UI's registry dependencies for the six Rust target
triples mapped by `Koh.Native/NativeBuild.targets`. The three release hosts are
Linux x64, Windows x64 and macOS ARM64. Catalog coverage is not evidence that all
six architectures have been built or run.

Each entry retains Cargo's license declaration, author metadata, source archive
URL/checksum, available VCS identity and the notice files supplied by that archive.
Archive SHA-256 values were checked against `Cargo.lock` before reading the files.
Missing monorepo notices were collected from upstream GitHub files at the
archive's VCS revision where available. Git blob identities were checked before
copying. Identical texts are stored once under their SHA-256; the manifest retains
the original filename and provenance for each use.

SPDX MIT and Apache-2.0 standard texts are included separately and unmodified.
Their template placeholders are not statements of copyright ownership. Author
metadata is retained as authorship, not inferred ownership. `mit_text_not_bundled`
records packages whose supplied texts do not contain a complete MIT grant;
some declare alternative licenses. This catalog does not select an alternative
or certify legal compliance.

Four archives supply a declaration without a standalone notice: `mac 0.1.1`,
`pathfinder_geometry 0.5.1`, `seahash 4.1.0` and `simd_helpers 0.1.0`. They are
marked `declaration_only`; no copyright holder or year was invented. Newer
Objective-C binding notices also describe their Apple SDK provenance. These
upstream notice questions and the repository's existing first-party attribution
question require release review before public distribution.

MPL components remain unmodified; their versioned source archive URLs and
checksums are recorded alongside the supplied notices. Rust standard-library
notices are staged separately from the compiler's actual sysroot by
`scripts/stage-rust-notices.py`.

Run `python scripts/verify-rust-ui-notices.py --target <triple>` from the checkout
to check coverage, lockfile identities and copied text hashes. This checks the
recorded evidence; a passing result is not release clearance. Dependency updates
need a fresh archive/upstream-notice review and catalog update.
