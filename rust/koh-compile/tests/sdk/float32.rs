use super::{build, report, support};
use rstest::rstest;
use std::path::Path;

#[rstest]
#[case("Debug", false)]
#[case("Release", true)]
fn sdk_compiles_actual_float_addition_without_managed_runtime_execution(
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let directory = tempfile::Builder::new()
        .prefix("koh float SDK ")
        .tempdir()
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let project = directory.path().join("Float game.csproj");
    std::fs::write(
        &project,
        r#"<Project>
<Import Project="$(KohRoot)src/Compiler/Koh.Sdk/Sdk/Sdk.props" />
<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
<Import Project="$(KohRoot)src/Compiler/Koh.Sdk/Sdk/Sdk.targets" />
</Project>"#,
    )
    .unwrap();
    std::fs::write(
        directory.path().join("Program.cs"),
        include_bytes!("../fixtures/Float32/Program.cs"),
    )
    .unwrap();
    let rom = directory.path().join("float output.gb");
    let object = directory.path().join("float output.o");
    let properties = vec![
        format!("-p:KohRoot={}/", root.display()),
        format!("-p:KohRomPath={}", rom.display()),
        format!("-p:KohObjectPath={}", object.display()),
        format!(
            "-p:KohCompilerExecutable={}",
            env!("CARGO_BIN_EXE_koh-compile")
        ),
        format!("-p:KohCgbCompatible={color}"),
    ];
    let output = build(&project, configuration, &properties);
    assert!(output.status.success(), "{}", report(&output));
    let bytes = std::fs::read(&rom).unwrap();
    assert_eq!(bytes[0x143] & 0x80 != 0, color);
    assert!(rom.with_extension("kdbg").is_file());
    let object = koh_objects::read_elf(&std::fs::read(object).unwrap()).unwrap();
    let linked = koh_link::link(
        &[object],
        &koh_link::LinkOptions {
            cgb: if color {
                koh_link::CgbMode::Compatible
            } else {
                koh_link::CgbMode::Preserve
            },
            ..Default::default()
        },
    )
    .unwrap();
    let stop = linked
        .symbols
        .iter()
        .find(|symbol| symbol.name == "__koh_exit")
        .unwrap()
        .value;
    let host = support::build_host();
    let output = support::dotnet()
        .arg(&host.dll)
        .args(["--native", &format!("{stop:x}")])
        .arg(&rom)
        .output()
        .unwrap();
    assert!(output.status.success(), "{}", report(&output));
    assert_eq!(
        String::from_utf8(output.stdout).unwrap().trim(),
        "00 40800000 D000 OK"
    );
}
