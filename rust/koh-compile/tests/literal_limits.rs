use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;
use std::{path::Path, process::Command};

#[rstest]
#[case("Debug")]
#[case("Release")]
fn readonly_literals_obey_the_cumulative_frontend_data_limit(#[case] configuration: &str) {
    let directory = tempfile::Builder::new()
        .prefix("koh literal limits ")
        .tempdir()
        .unwrap();
    let project = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests/fixtures/LiteralLimits/LiteralLimits.csproj");
    std::fs::copy(project, directory.path().join("LiteralLimits.csproj")).unwrap();
    let term = format!("sum += \"{}\"u8[0];", "K".repeat(4095));
    let method = |name, count| {
        format!(
            "public static int {name}() {{ int sum = 0; {} return sum; }}",
            term.repeat(count)
        )
    };
    let source = format!(
        "public static class Program {{ {} {} }}",
        method("AtLimit", 16),
        method("BeyondLimit", 17)
    );
    std::fs::write(directory.path().join("Program.cs"), source).unwrap();
    let output = Command::new(std::env::var_os("KOH_DOTNET").unwrap_or("dotnet".into()))
        .arg("build")
        .arg(directory.path().join("LiteralLimits.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let image = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/LiteralLimits.dll")),
    )
    .unwrap();
    let references = std::fs::read_to_string(
        directory
            .path()
            .join(format!("obj/{configuration}/net10.0/reference-paths.txt")),
    )
    .unwrap();
    let runtime = references
        .lines()
        .map(Path::new)
        .find(|p| {
            p.file_name()
                .is_some_and(|name| name == "System.Runtime.dll")
        })
        .unwrap();
    let runtime = std::fs::read(runtime).unwrap();
    let resolver = Resolver::new(&[&image, &runtime]).unwrap();
    let object = compile::object(&resolver, Some("Program::AtLimit")).unwrap();
    let mut bytes = vec![b'K'; 4095];
    bytes.push(0);
    assert!(
        object
            .sections
            .iter()
            .any(|section| section.name.starts_with(".rodata.") && section.data == bytes)
    );
    let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
    koh_link::link(&[object], &Default::default()).unwrap();
    let error = compile::object(&resolver, Some("Program::BeyondLimit"))
        .err()
        .expect("More than64KiB of materialized method literals must diagnose");
    assert!(
        error.contains("literal data exceeds 64 KiB budget")
            && error.contains("BeyondLimit")
            && error.contains("IL_"),
        "{error}"
    );
}
