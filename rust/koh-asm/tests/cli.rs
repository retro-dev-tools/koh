use std::{
    fs,
    path::{Path, PathBuf},
    process::{Command, Output},
    sync::atomic::{AtomicU64, Ordering},
};

static NEXT: AtomicU64 = AtomicU64::new(0);

struct Fixture(PathBuf);
impl Fixture {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "koh-cli-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        Self(path)
    }
    fn path(&self, name: &str) -> PathBuf {
        self.0.join(name)
    }
    fn run(&self, args: &[&str]) -> Output {
        Command::new(env!("CARGO_BIN_EXE_koh-asm"))
            .current_dir(&self.0)
            .args(args)
            .output()
            .unwrap()
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn assert_success(output: Output) {
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
}

fn no_temporary_files(path: &Path) {
    assert!(fs::read_dir(path).unwrap().all(|entry| {
        !entry
            .unwrap()
            .file_name()
            .to_string_lossy()
            .starts_with(".koh-asm-")
    }));
}

#[test]
fn help_version_and_argument_diagnostics() {
    let fixture = Fixture::new();
    let help = fixture.run(&["--help"]);
    assert!(String::from_utf8_lossy(&help.stdout).contains("Usage: koh-asm"));
    assert_success(help);
    let version = fixture.run(&["--version"]);
    assert_eq!(
        String::from_utf8_lossy(&version.stdout).trim(),
        format!("koh-asm {}", env!("CARGO_PKG_VERSION"))
    );
    assert_success(version);
    for args in [
        vec![],
        vec!["--bogus"],
        vec!["--output"],
        vec!["--format"],
        vec!["--format", "bad"],
        vec!["a.asm", "b.asm"],
        vec!["missing.asm"],
    ] {
        let output = fixture.run(&args);
        assert!(!output.status.success(), "{args:?}");
        assert!(!output.stderr.is_empty(), "{args:?}");
    }
}

#[test]
fn formats_defaults_and_output_aliases() {
    let fixture = Fixture::new();
    fs::write(
        fixture.path("input.asm"),
        "SECTION \"test\", ROM0[$100]\n nop\n ret\n",
    )
    .unwrap();
    assert_success(fixture.run(&["input.asm"]));
    let kobj = fs::read(fixture.path("input.kobj")).unwrap();
    assert!(!kobj.is_empty());
    for format in ["rgbds", "o"] {
        assert_success(fixture.run(&["input.asm", "--format", format]));
        assert!(
            fs::read(fixture.path("input.o"))
                .unwrap()
                .starts_with(b"RGB9")
        );
    }
    assert_success(fixture.run(&["input.asm", "-f", "kobj", "-o", "custom.kobj"]));
    assert_eq!(fs::read(fixture.path("custom.kobj")).unwrap(), kobj);
    assert_success(fixture.run(&["input.asm", "--output", "custom.o", "-f", "rgbds"]));
    assert!(
        fs::read(fixture.path("custom.o"))
            .unwrap()
            .starts_with(b"RGB9")
    );
    no_temporary_files(&fixture.0);
}

#[test]
fn failed_assembly_preserves_existing_output_and_reports_source_location() {
    let fixture = Fixture::new();
    fs::write(
        fixture.path("input.asm"),
        "SECTION \"test\", ROM0\n ld a,256\n",
    )
    .unwrap();
    fs::write(fixture.path("result.kobj"), b"existing artifact").unwrap();
    let output = fixture.run(&["input.asm", "-o", "result.kobj"]);
    assert!(!output.status.success());
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(stderr.contains("input.asm:2:1: error:"), "{stderr}");
    assert!(stderr.contains("out of range"), "{stderr}");
    assert_eq!(
        fs::read(fixture.path("result.kobj")).unwrap(),
        b"existing artifact"
    );
    no_temporary_files(&fixture.0);
}

#[test]
fn successful_assembly_replaces_output_and_refuses_source_overwrite() {
    let fixture = Fixture::new();
    let source = "SECTION \"test\", ROM0\n nop\n";
    fs::write(fixture.path("input.asm"), source).unwrap();
    fs::write(fixture.path("output.o"), b"existing artifact").unwrap();
    assert_success(fixture.run(&["input.asm", "-f", "rgbds", "-o", "output.o"]));
    assert!(
        fs::read(fixture.path("output.o"))
            .unwrap()
            .starts_with(b"RGB9")
    );
    let output = fixture.run(&["input.asm", "-o", "input.asm"]);
    assert!(!output.status.success());
    assert!(String::from_utf8_lossy(&output.stderr).contains("overwrite input source"));
    assert_eq!(
        fs::read_to_string(fixture.path("input.asm")).unwrap(),
        source
    );
    no_temporary_files(&fixture.0);
}

#[test]
fn failed_output_rename_preserves_destination_and_removes_temporary_file() {
    let fixture = Fixture::new();
    fs::write(fixture.path("input.asm"), "SECTION \"test\", ROM0\n nop\n").unwrap();
    fs::create_dir(fixture.path("destination")).unwrap();
    fs::write(fixture.path("destination/keep"), b"keep").unwrap();
    let output = fixture.run(&["input.asm", "-o", "destination"]);
    assert!(!output.status.success());
    assert!(String::from_utf8_lossy(&output.stderr).contains("cannot write"));
    assert_eq!(fs::read(fixture.path("destination/keep")).unwrap(), b"keep");
    no_temporary_files(&fixture.0);
}
