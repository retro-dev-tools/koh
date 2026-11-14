use koh_compile::cil::{Operand, decode};
use std::{collections::HashSet, path::Path, process::Command};

#[test]
fn encodings_match_the_dotnet_opcode_catalog() {
    let directory = tempfile::Builder::new()
        .prefix("koh opcode oracle ")
        .tempdir()
        .unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Reader");
    for name in ["OpcodeOracle.csproj", "OpcodeOracle.cs"] {
        std::fs::copy(fixture.join(name), directory.path().join(name)).unwrap();
    }
    let dotnet = std::env::var_os("KOH_DOTNET").unwrap_or("dotnet".into());
    let build = Command::new(&dotnet)
        .arg("build")
        .arg(directory.path().join("OpcodeOracle.csproj"))
        .args(["-c", "Release", "--nologo", "-v:q"])
        .output()
        .expect("Opcode oracle tests require the .NET 10 SDK");
    assert!(
        build.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&build.stdout),
        String::from_utf8_lossy(&build.stderr)
    );
    let catalog = Command::new(&dotnet)
        .arg("exec")
        .arg(
            directory
                .path()
                .join("bin/Release/net10.0/OpcodeOracle.dll"),
        )
        .output()
        .unwrap();
    assert!(
        catalog.status.success(),
        "{}",
        String::from_utf8_lossy(&catalog.stderr)
    );
    let catalog = String::from_utf8(catalog.stdout).unwrap();
    let mut count = 0;
    let mut accepted = HashSet::new();
    for line in catalog.lines() {
        let (opcode, encoding) = line.split_once(' ').unwrap();
        let opcode = u16::from_str_radix(opcode, 16).unwrap();
        assert!(accepted.insert(opcode));
        let width = match encoding {
            "InlineNone" => 0,
            "ShortInlineVar" | "ShortInlineI" | "ShortInlineBrTarget" => 1,
            "InlineVar" => 2,
            "InlineI" | "ShortInlineR" | "InlineBrTarget" | "InlineSwitch" | "InlineField"
            | "InlineMethod" | "InlineSig" | "InlineString" | "InlineTok" | "InlineType" => 4,
            "InlineI8" | "InlineR" => 8,
            _ => panic!("Unexpected .NET operand encoding {encoding}"),
        };
        let mut bytes = if opcode > 0xff {
            vec![0xfe, opcode as u8]
        } else {
            vec![opcode as u8]
        };
        let end = bytes.len() + width;
        bytes.resize(end, 0);
        bytes.push(0x2a);
        let decoded = decode(&bytes).unwrap_or_else(|error| panic!("{line}: {error}"));
        assert_eq!(decoded.len(), 2, "{line}");
        assert_eq!(decoded[0].opcode, opcode, "{line}");
        assert_eq!(decoded[1].offset, end as u32, "{line}");
        let correct_kind = match encoding {
            "InlineNone" => matches!(decoded[0].operand, Operand::None),
            "ShortInlineVar" | "InlineVar" => matches!(decoded[0].operand, Operand::Variable(_)),
            "ShortInlineI" | "InlineI" | "InlineI8" => {
                matches!(decoded[0].operand, Operand::Integer(_))
            }
            "ShortInlineR" => matches!(decoded[0].operand, Operand::Float32(_)),
            "InlineR" => matches!(decoded[0].operand, Operand::Float64(_)),
            "ShortInlineBrTarget" | "InlineBrTarget" => {
                matches!(decoded[0].operand, Operand::Target(_))
            }
            "InlineSwitch" => matches!(decoded[0].operand, Operand::Targets(_)),
            _ => matches!(decoded[0].operand, Operand::Token(_)),
        };
        assert!(correct_kind, "{line}: {:?}", decoded[0].operand);
        count += 1;
    }
    assert_eq!(count, 218);
    for opcode in (0u16..=255).chain(0xfe00..=0xfeff) {
        if accepted.contains(&opcode) || opcode == 0xfe || opcode == 0xfe19 {
            continue;
        }
        let mut bytes = if opcode > 255 {
            vec![0xfe, opcode as u8]
        } else {
            vec![opcode as u8]
        };
        bytes.extend_from_slice(&[0; 16]);
        assert!(
            decode(&bytes).is_err(),
            "Reserved opcode {opcode:04x} accepted"
        );
    }
}
