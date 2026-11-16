use super::{Host, gameboy_images, host};
use rstest::rstest;
use std::{ffi::OsString, path::Path, process::Command};

#[rstest]
#[case("Debug")]
#[case("Release")]
fn cli_discovers_optional_symbols_and_rejects_bad_or_aliased_inputs(
    host: &Host,
    #[case] configuration: &str,
) {
    let (directory, _) = gameboy_images(host, configuration, "SourceDebug");
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let assembly = output.join("SourceDebug.dll");
    let reference = output.join("Koh.GameBoy.dll");
    let pdb = assembly.with_extension("pdb");
    let saved = std::fs::read(&pdb).unwrap();
    let explicit = directory.path().join("explicit source symbols.pdb");
    std::fs::write(&explicit, &saved).unwrap();
    let target = directory.path().join("source object.o");
    let run = |flags: &[OsString], target: &Path| {
        Command::new(env!("CARGO_BIN_EXE_koh-compile"))
            .arg(&assembly)
            .arg("-r")
            .arg(&reference)
            .arg("-o")
            .arg(target)
            .args(flags)
            .output()
            .unwrap()
    };
    let inspect = || koh_objects::read_elf(&std::fs::read(&target).unwrap()).unwrap();
    let result = run(&[], &target);
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert!(
        inspect()
            .sections
            .iter()
            .flat_map(|s| &s.lines)
            .any(|line| line.file.ends_with("mapped source.cs"))
    );
    let result = run(&["--pdb".into(), explicit.as_os_str().into()], &target);
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    let result = run(&["--pdb".into(), explicit.as_os_str().into()], &explicit);
    assert!(!result.status.success());
    assert_eq!(std::fs::read(&explicit).unwrap(), saved);
    let result = run(
        &[
            "--no-pdb".into(),
            "--pdb".into(),
            explicit.as_os_str().into(),
        ],
        &target,
    );
    assert!(!result.status.success());
    std::fs::write(&pdb, b"invalid symbols").unwrap();
    let before = std::fs::read(&target).unwrap();
    let result = run(&[], &target);
    assert!(!result.status.success());
    assert!(String::from_utf8_lossy(&result.stderr).contains("Portable PDB"));
    assert_eq!(std::fs::read(&target).unwrap(), before);
    let result = run(&["--no-pdb".into()], &target);
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert!(inspect().sections.iter().all(|s| s.lines.is_empty()));
    std::fs::remove_file(&pdb).unwrap();
    std::fs::remove_file(reference.with_extension("pdb")).unwrap();
    let result = run(&[], &target);
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert!(inspect().sections.iter().all(|s| s.lines.is_empty()));
    let result = run(&["--pdb".into(), explicit.as_os_str().into()], &target);
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert!(inspect().sections.iter().any(|s| !s.lines.is_empty()));
}
