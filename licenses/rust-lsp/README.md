# Rust language-server notices

These exact crate-archive license texts cover the language server's two added
YAML dependencies. Archive checksums match Cargo.lock; manifest.json records
the upstream notice paths and SHA-256 hashes. Both packages declare MIT.

Native language-server packages also retain the existing rust-ui and
rust-assembler catalogs for the shared URL/Unicode/Serde and worker dependency
graphs, plus rust-runtime notices copied from the actual compiler sysroot.
Those catalogs are retained as supersets; inclusion does not imply every listed
package is linked into koh-lsp. Keep all accompanying notice directories.

This inventory does not settle existing first-party or whole-toolchain release
licensing questions. No release publication is part of this migration.
