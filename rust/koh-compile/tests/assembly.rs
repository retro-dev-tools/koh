use koh_compile::assembly::Assembly;
use rstest::{fixture, rstest};
use std::{path::Path, process::Command};

struct Assemblies {
    bytes: [Vec<u8>; 3],
}

#[fixture]
#[once]
fn assemblies() -> Assemblies {
    let directory = tempfile::Builder::new()
        .prefix("koh compiler fixtures ")
        .tempdir()
        .unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Reader");
    for name in ["Reader.csproj", "Program.cs"] {
        std::fs::copy(fixture.join(name), directory.path().join(name)).unwrap();
    }
    let bytes = [
        ("Debug", "AnyCPU"),
        ("Release", "AnyCPU"),
        ("Release", "x64"),
    ]
    .map(|(configuration, platform)| {
        let output = Command::new(std::env::var_os("KOH_DOTNET").unwrap_or("dotnet".into()))
            .arg("build")
            .arg(directory.path().join("Reader.csproj"))
            .args(["-c", configuration, "--nologo", "-v:q"])
            .arg(format!("-p:PlatformTarget={platform}"))
            .output()
            .expect("The compiler tests require a .NET 10 SDK (KOH_DOTNET may select it)");
        assert!(
            output.status.success(),
            "Roslyn {configuration} fixture build failed: {}\n{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
        std::fs::read(
            directory
                .path()
                .join(format!("bin/{configuration}/net10.0/Reader.dll")),
        )
        .unwrap()
    });
    Assemblies { bytes }
}

#[rstest]
#[case(0)]
#[case(1)]
#[case(2)]
fn reads_roslyn_assembly(assemblies: &Assemblies, #[case] configuration: usize) {
    let assembly = Assembly::parse(&assemblies.bytes[configuration]).unwrap();
    assert_eq!(assembly.name().unwrap(), "Reader");
    assert_eq!(
        assembly.method_name(assembly.entry_point).unwrap(),
        "Program::Main"
    );
    let body = assembly.method_body(assembly.entry_point).unwrap();
    assert!(!body.il.is_empty());
    assert_eq!(body.il.last(), Some(&0x2a));
    assert!(!body.extra_sections);
    assert!(assembly.metadata().assembly_refs.iter().any(|reference| {
        assembly.metadata().strings.get(reference.name).unwrap() == "System.Runtime"
    }));
    let names: Vec<_> = (1..=assembly.metadata().method_defs.len())
        .map(|index| assembly.method_name(0x06000000 | index as u32).unwrap())
        .collect();
    let add = names
        .iter()
        .position(|name| name == "Program::Add")
        .unwrap();
    let add = assembly.method_body(0x06000001 + add as u32).unwrap();
    assert!(add.il.contains(&0x58));
    for method in 1..=assembly.metadata().method_defs.len() {
        let body = assembly.method_body(0x06000000 | method as u32).unwrap();
        koh_compile::cil::decode(body.il).unwrap();
    }
    let exception = names
        .iter()
        .position(|name| name == "Program::WithException")
        .unwrap();
    assert!(
        assembly
            .method_body(0x06000001 + exception as u32)
            .unwrap()
            .extra_sections
    );
    for token in [0, 0x06000000, 0x0600ffff, 0x0a000001] {
        assert!(assembly.method(token).is_err(), "token {token:08x}");
    }
}

#[rstest]
fn truncation_and_header_mutations_do_not_panic(assemblies: &Assemblies) {
    let bytes = &assemblies.bytes[0];
    for length in 0..bytes.len() {
        let _ = Assembly::parse(&bytes[..length]);
    }
    for index in 0..512.min(bytes.len()) {
        let mut mutated = bytes.clone();
        mutated[index] ^= 0xff;
        if let Ok(assembly) = Assembly::parse(&mutated) {
            for method in 1..=assembly.metadata().method_defs.len() {
                let _ = assembly.method_body(0x06000000 | method as u32);
            }
        }
    }
}

#[rstest]
fn rejects_invalid_local_signature_references(assemblies: &Assemblies) {
    let bytes = &assemblies.bytes[0];
    let assembly = Assembly::parse(bytes).unwrap();
    let body = assembly.method_body(assembly.entry_point).unwrap();
    assert_ne!(body.local_signature, 0);
    let il_offset = body.il.as_ptr() as usize - bytes.as_ptr() as usize;
    for token in [0x11000000u32, 0x1100ffff, 0x06000001] {
        let mut mutated = bytes.clone();
        mutated[il_offset - 4..il_offset].copy_from_slice(&token.to_le_bytes());
        let assembly = Assembly::parse(&mutated).unwrap();
        assert!(
            assembly.method_body(assembly.entry_point).is_err(),
            "local token {token:08x}"
        );
    }
}

#[rstest]
#[case(b"")]
#[case(b"not a PE file")]
#[case(b"MZ")]
fn rejects_invalid_images(#[case] bytes: &[u8]) {
    assert!(Assembly::parse(bytes).is_err());
}
