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
fn koh_empty_warn_extension_remains_nonfatal() {
    let fixture = Fixture::new();
    fs::write(fixture.path("warn.asm"), "WARN\nSECTION \"code\",ROM0\nnop").unwrap();
    let output = fixture.run(&["warn.asm"]);
    assert!(output.status.success());
    assert!(
        String::from_utf8_lossy(&output.stderr).contains("warn.asm:1:1: warning: WARN directive")
    );
    assert!(fixture.path("warn.o").exists());
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
    let elf = fs::read(fixture.path("input.o")).unwrap();
    assert!(elf.starts_with(b"\x7fELF"));
    let object = koh_asm::object::read_elf(&elf).unwrap();
    assert_eq!(object.sections[0].data, [0, 0xc9]);
    assert_success(fixture.run(&["input.asm", "-o", "custom.o"]));
    assert_eq!(fs::read(fixture.path("custom.o")).unwrap(), elf);
    for format in ["kobj", "rgbds", "o"] {
        assert!(
            !fixture
                .run(&["input.asm", "--format", format])
                .status
                .success()
        );
    }
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
    fs::write(fixture.path("result.o"), b"existing artifact").unwrap();
    let output = fixture.run(&["input.asm", "-o", "result.o"]);
    assert!(!output.status.success());
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(stderr.contains("input.asm:2:1: error:"), "{stderr}");
    assert!(stderr.contains("out of range"), "{stderr}");
    assert_eq!(
        fs::read(fixture.path("result.o")).unwrap(),
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
    assert_success(fixture.run(&["input.asm", "-o", "output.o"]));
    assert!(
        fs::read(fixture.path("output.o"))
            .unwrap()
            .starts_with(b"\x7fELF")
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

#[test]
fn structured_warnings_are_printed_once_without_error_labels() {
    let fixture = Fixture::new();
    fs::write(fixture.path("warnings.asm"),"WARN \"custom\"\nASSERT WARN,0,\"detail\"\nASSERT WARN,1,\"not emitted\"\nSECTION \"code\",ROM0\nnop\n").unwrap();
    let output = fixture.run(&["warnings.asm"]);
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(output.status.success(), "{stderr}");
    assert!(
        stderr.contains("warnings.asm:1:1: warning: custom"),
        "{stderr}"
    );
    assert!(
        stderr.contains("warnings.asm:2:1: warning: Assertion failed: detail"),
        "{stderr}"
    );
    assert_eq!(
        stderr
            .lines()
            .filter(|line| line.contains(": warning:"))
            .count(),
        2,
        "{stderr}"
    );
    assert!(!stderr.contains(": error:"), "{stderr}");
    assert!(!stderr.contains("not emitted"), "{stderr}");
    fs::write(
        fixture.path("errors.asm"),
        "WARN \"retained\"\nSECTION \"code\",ROM0\nld a,256",
    )
    .unwrap();
    let failed = fixture.run(&["errors.asm"]);
    assert!(!failed.status.success());
    let stderr = String::from_utf8_lossy(&failed.stderr);
    assert!(stderr.contains(": warning: retained"), "{stderr}");
    assert!(stderr.contains(": error:"), "{stderr}");
}
