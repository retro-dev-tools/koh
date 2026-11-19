use super::{build, report, support};
use rstest::rstest;
use std::path::Path;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn sdk_builds_iterators_and_finally_from_standard_references(
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let directory = tempfile::Builder::new()
        .prefix("koh iterator SDK ")
        .tempdir()
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let project = directory.path().join("Iterator game.csproj");
    std::fs::write(
        &project,
        r#"<Project>
<Import Project="$(KohRoot)src/Compiler/Koh.Sdk/Sdk/Sdk.props" />
<PropertyGroup><TargetFramework>net10.0</TargetFramework><AllowUnsafeBlocks>true</AllowUnsafeBlocks></PropertyGroup>
<Import Project="$(KohRoot)src/Compiler/Koh.Sdk/Sdk/Sdk.targets" />
</Project>"#,
    )
    .unwrap();
    std::fs::write(
        directory.path().join("Program.cs"),
        r#"using System.Collections.Generic;
using Koh.GameBoy;
[System.Runtime.CompilerServices.SkipLocalsInit]
public static unsafe class Program {
    private static IEnumerable<byte> Steps(byte count) {
        for (byte i = 0; i < count; i++) yield return i;
    }
    public static int Main() {
        int sum = 0;
        try { foreach (byte value in Steps(5)) sum += value; }
        finally { Hardware.BGP = (byte)(sum + 2); }
        return Hardware.BGP + 30;
    }
}"#,
    )
    .unwrap();
    let rom = directory.path().join("iterator output.gb");
    let object = directory.path().join("iterator output.o");
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
    let result = build(&project, configuration, &properties);
    assert!(result.status.success(), "{}", report(&result));
    let bytes = std::fs::read(&rom).unwrap();
    assert_eq!(bytes[0x143] & 0x80 != 0, color);
    assert!(rom.with_extension("elf").is_file());
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
        .find(|s| s.name == "__koh_exit")
        .unwrap();
    let host = support::build_host();
    let result = host
        .command()
        .args([
            "--native-heap",
            &format!("{:x}:{:x}", stop.bank.unwrap_or(0), stop.value),
        ])
        .arg(&rom)
        .output()
        .unwrap();
    assert!(result.status.success(), "{}", report(&result));
    let text = String::from_utf8(result.stdout).unwrap();
    let fields: Vec<_> = text.split_whitespace().collect();
    assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"]);
}
