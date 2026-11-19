use super::{build, report, support};
use rstest::rstest;
use std::path::Path;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn sdk_builds_deferred_linq_from_standard_references(
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let directory = tempfile::Builder::new()
        .prefix("koh LINQ SDK ")
        .tempdir()
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let project = directory.path().join("Query game.csproj");
    std::fs::write(&project,r#"<Project>
<Import Project="$(KohRoot)src/Compiler/Koh.Sdk/Sdk/Sdk.props" />
<PropertyGroup><TargetFramework>net10.0</TargetFramework><AllowUnsafeBlocks>true</AllowUnsafeBlocks></PropertyGroup>
<Import Project="$(KohRoot)src/Compiler/Koh.Sdk/Sdk/Sdk.targets" />
</Project>"#).unwrap();
    std::fs::write(
        directory.path().join("Program.cs"),
        r#"using System.Collections.Generic;
using System.Linq;
using Koh.GameBoy;
public static unsafe class Program {
    private static IEnumerable<int> Steps() {
        for (int i = 1; i <= 5; i++) yield return i;
    }
    public static int Main() {
        int threshold = 2;
        var query = Steps().Where(x => x > threshold).Select(x => x * 2);
        Hardware.BGP = (byte)query.Sum();
        int* p = (int*)0xDF00;
        p[0] = query.Count(); p[1] = query.Max();
        return Hardware.BGP + 18;
    }
}"#,
    )
    .unwrap();
    let rom = directory.path().join("query output.gb");
    let object_path = directory.path().join("query output.o");
    let properties = vec![
        format!("-p:KohRoot={}/", root.display()),
        format!("-p:KohRomPath={}", rom.display()),
        format!("-p:KohObjectPath={}", object_path.display()),
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
    let debug = std::fs::read(rom.with_extension("elf")).unwrap();
    assert!(debug.starts_with(b"\x7fELF"));
    let modified = std::fs::metadata(&rom).unwrap().modified().unwrap();
    let result = build(&project, configuration, &properties);
    assert!(result.status.success(), "{}", report(&result));
    assert_eq!(
        std::fs::metadata(&rom).unwrap().modified().unwrap(),
        modified,
        "incremental ROM output"
    );
    assert_eq!(std::fs::read(&rom).unwrap(), bytes);
    let object = koh_objects::read_elf(&std::fs::read(&object_path).unwrap()).unwrap();
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
    assert_eq!(linked.rom, bytes, "SDK uses canonical linker result");
    for file in ["Program.cs", "NativeLinq.cs"] {
        assert!(
            linked
                .lines
                .iter()
                .any(|l| l.file.ends_with(file) && l.line > 0),
            "{file}"
        );
    }
    let stop = linked
        .symbols
        .iter()
        .find(|s| s.name == "__koh_exit")
        .unwrap();
    let host = support::build_host();
    let result = host
        .command()
        .args([
            "--native-arena",
            &format!("{:x}:{:x}", stop.bank.unwrap_or(0), stop.value),
        ])
        .arg(&rom)
        .output()
        .unwrap();
    assert!(result.status.success(), "{}", report(&result));
    let text = String::from_utf8(result.stdout).unwrap();
    let fields: Vec<_> = text.split_whitespace().collect();
    assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"]);
    assert_eq!(fields.last(), Some(&"18"), "actual BGP");
    let mut packet = [0x5a; 256];
    packet[..4].copy_from_slice(&3i32.to_le_bytes());
    packet[4..8].copy_from_slice(&10i32.to_le_bytes());
    let expected: String = packet.iter().map(|b| format!("{b:02X}")).collect();
    assert_eq!(&fields[4][0xf00 * 2..], expected);
    if color {
        assert_eq!(fields[5], "6B".repeat(4096));
    }
}
