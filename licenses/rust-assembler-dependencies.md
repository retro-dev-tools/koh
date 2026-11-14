# Rust assembler dependency notices

Redistributions containing `koh-asm` must retain this notice and the accompanying
`rust-assembler/` license texts. This inventory covers every external package in
the repository's `Cargo.lock`, including host-side procedural macro dependencies.
It does not assert that every listed package is linked into every target binary.

MIT is selected wherever the package offers an MIT alternative. For
`unicode-ident`, the additional Unicode-3.0 terms also apply. The selected MIT
terms require retaining the copyright and permission notices; the Unicode terms
require their copyright and permission notice with copies or associated
documentation. The complete texts below retain the shipped notices and warranty
disclaimers.

| Package | Version | Declared license | Selected terms and full text |
| --- | --- | --- | --- |
| [itoa](https://docs.rs/crate/itoa/1.0.19/source/Cargo.toml) | 1.0.19 | MIT OR Apache-2.0 | [MIT](rust-assembler/MIT.txt) |
| [memchr](https://docs.rs/crate/memchr/2.8.3/source/Cargo.toml) | 2.8.3 | Unlicense OR MIT | [MIT, including its copyright notice](rust-assembler/memchr-MIT.txt) |
| [proc-macro2](https://docs.rs/crate/proc-macro2/1.0.107/source/Cargo.toml) | 1.0.107 | MIT OR Apache-2.0 | [MIT](rust-assembler/MIT.txt) |
| [quote](https://docs.rs/crate/quote/1.0.47/source/Cargo.toml) | 1.0.47 | MIT OR Apache-2.0 | [MIT](rust-assembler/MIT.txt) |
| [serde](https://docs.rs/crate/serde/1.0.229/source/Cargo.toml) | 1.0.229 | MIT OR Apache-2.0 | [MIT](rust-assembler/MIT.txt) |
| [serde_core](https://docs.rs/crate/serde_core/1.0.229/source/Cargo.toml) | 1.0.229 | MIT OR Apache-2.0 | [MIT](rust-assembler/MIT.txt) |
| [serde_derive](https://docs.rs/crate/serde_derive/1.0.229/source/Cargo.toml) | 1.0.229 | MIT OR Apache-2.0 | [MIT](rust-assembler/MIT.txt) |
| [serde_json](https://docs.rs/crate/serde_json/1.0.154/source/Cargo.toml) | 1.0.154 | MIT OR Apache-2.0 | [MIT](rust-assembler/MIT.txt), plus the attribution below |
| [syn](https://docs.rs/crate/syn/3.0.10/source/Cargo.toml) | 3.0.10 | MIT OR Apache-2.0 | [MIT](rust-assembler/MIT.txt) |
| [unicode-ident](https://docs.rs/crate/unicode-ident/1.0.27/source/Cargo.toml) | 1.0.27 | (MIT OR Apache-2.0) AND Unicode-3.0 | [MIT](rust-assembler/MIT.txt) AND [Unicode-3.0](rust-assembler/Unicode-3.0.txt) |
| [zmij](https://docs.rs/crate/zmij/1.0.23/source/Cargo.toml) | 1.0.23 | MIT | [MIT](rust-assembler/MIT.txt) |

Package metadata credits David Tolnay for `itoa`, `quote`, `syn`, `unicode-ident`
and `zmij`; David Tolnay and Alex Crichton for `proc-macro2`; Erick Tryzelaar and
David Tolnay for `serde`, `serde_core` and `serde_derive`; and Andrew Gallant and
bluss for `memchr`.

The float-parsing code in `serde_json`'s `src/lexical` is derived from Alexander
Huszagh's `lexical` crate: **copyright Alexander Huszagh**. That module offers the
same MIT or Apache-2.0 choice as `serde_json`; MIT is selected here.

The ten packages linked to the common MIT text ship byte-identical
`LICENSE-MIT` files. `memchr`'s separate text retains `Copyright (c) 2015 Andrew
Gallant`. The Unicode text is copied from `unicode-ident` 1.0.27 and retains its
shipped `Copyright © 1991-2023 Unicode, Inc.` notice; it is not replaced with a
newer website copy. None of these packages contains an upstream `NOTICE` file.

The audited registry archive SHA-256 values matched `Cargo.lock`. Each copied
license and each audited package manifest was also compared with the matching
file inside that archive. No upstream source code is included in this bundle.
When the lockfile changes, review the new package artifacts and update this
inventory and license texts together.

This inventory covers the Cargo dependencies above. Rust's standard library and
target runtime/toolchain components are outside `Cargo.lock` and are not audited
by this manifest; their notices are handled separately by release policy. This
document does not replace Koh's own license or other bundled components' notices.
