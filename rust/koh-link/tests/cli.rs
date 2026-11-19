use koh_objects::*;
use std::{
    io::Write,
    process::{Command, Stdio},
};

fn fixture() -> Object {
    Object {
        sections: vec![Section {
            name: "code".into(),
            size: 2,
            data: vec![0x18, 0xfe],
            lines: vec![Line {
                offset: 0,
                count: 2,
                file: "input.asm".into(),
                line: 1,
                expansion: vec![],
            }],
            ..Default::default()
        }],
        symbols: vec![Symbol {
            name: "Start".into(),
            binding: SymbolBinding::Exported,
            definition: SymbolDefinition::Label {
                section: SectionId(0),
                offset: 0,
            },
            source: None,
        }],
    }
}
fn directory(name: &str) -> std::path::PathBuf {
    let path = std::env::temp_dir().join(format!("koh-link-{}-{name}", std::process::id()));
    std::fs::create_dir_all(&path).unwrap();
    path
}
#[test]
fn json_to_elf_to_rom_sym_debug_and_report() {
    let dir = directory("roundtrip");
    let elf = dir.join("code.o");
    let rom = dir.join("code.gb");
    let sym = dir.join("code.sym");
    let debug = dir.join("code.elf");
    let report = dir.join("code.json");
    let mut child = Command::new(env!("CARGO_BIN_EXE_koh-link"))
        .args(["--emit-elf", "-o"])
        .arg(&elf)
        .stdin(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child
        .stdin
        .take()
        .unwrap()
        .write_all(&serde_json::to_vec(&fixture()).unwrap())
        .unwrap();
    let result = child.wait_with_output().unwrap();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    let bytes = std::fs::read(&elf).unwrap();
    assert_eq!(&bytes[..4], b"\x7fELF");
    let result = Command::new(env!("CARGO_BIN_EXE_koh-link"))
        .arg("-o")
        .arg(&rom)
        .arg("-n")
        .arg(&sym)
        .arg("-d")
        .arg(&debug)
        .arg("--report-json")
        .arg(&report)
        .args(["--raw", "--size=10"])
        .arg(&elf)
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    let bytes = std::fs::read(&rom).unwrap();
    assert_eq!(bytes.len(), 16);
    assert_eq!(&bytes[..2], &[0x18, 0xfe]);
    assert!(
        std::fs::read_to_string(&sym)
            .unwrap()
            .contains("00:0000 Start")
    );
    assert_eq!(&std::fs::read(&debug).unwrap()[..4], b"\x7fELF");
    let value: serde_json::Value = serde_json::from_slice(&std::fs::read(report).unwrap()).unwrap();
    assert_eq!(value["symbols"][0]["value"], 0);
    assert_eq!(value["lines"][0]["file"], "input.asm");
    std::fs::remove_dir_all(dir).unwrap();
}
#[test]
fn corrupt_input_preserves_existing_output() {
    let dir = directory("corrupt");
    let elf = dir.join("bad.o");
    let rom = dir.join("existing.gb");
    std::fs::write(&elf, b"bad ELF").unwrap();
    std::fs::write(&rom, b"preserve").unwrap();
    let result = Command::new(env!("CARGO_BIN_EXE_koh-link"))
        .arg("-o")
        .arg(&rom)
        .arg(&elf)
        .output()
        .unwrap();
    assert!(!result.status.success());
    assert_eq!(std::fs::read(&rom).unwrap(), b"preserve");
    std::fs::remove_dir_all(dir).unwrap();
}
#[test]
fn alias_output_cannot_overwrite_input() {
    let dir = directory("alias");
    let elf = dir.join("input.o");
    let bytes = write_elf(&fixture()).unwrap();
    std::fs::write(&elf, &bytes).unwrap();
    let result = Command::new(env!("CARGO_BIN_EXE_koh-link"))
        .arg("-o")
        .arg(dir.join(".").join("input.o"))
        .arg(&elf)
        .output()
        .unwrap();
    assert!(!result.status.success());
    assert!(String::from_utf8_lossy(&result.stderr).contains("distinct"));
    assert_eq!(std::fs::read(&elf).unwrap(), bytes);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn long_aliases_version_and_default_outputs_with_spaces() {
    let dir = directory("paths with spaces");
    let elf = dir.join("input with spaces.o");
    std::fs::write(&elf, write_elf(&fixture()).unwrap()).unwrap();
    let result = Command::new(env!("CARGO_BIN_EXE_koh-link"))
        .arg("--version")
        .output()
        .unwrap();
    assert!(result.status.success());
    assert!(String::from_utf8_lossy(&result.stdout).starts_with("koh-link "));
    let result = Command::new(env!("CARGO_BIN_EXE_koh-link"))
        .args(["--raw", "--size=10"])
        .arg(&elf)
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert_eq!(std::fs::read(elf.with_extension("bin")).unwrap().len(), 16);
    let output = dir.join("explicit.gb");
    let result = Command::new(env!("CARGO_BIN_EXE_koh-link"))
        .arg("--output")
        .arg(&output)
        .arg("--sym")
        .arg(dir.join("explicit.sym"))
        .arg("--debug")
        .arg(dir.join("explicit.elf"))
        .arg(&elf)
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert_eq!(std::fs::read(output).unwrap().len(), 0x8000);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn separate_processes_produce_identical_elf_and_link_artifacts() {
    let dir = directory("deterministic");
    let mut consumer = fixture();
    consumer.sections[0].address = Some(0x100);
    consumer.symbols.push(Symbol {
        name: "loop".into(),
        binding: SymbolBinding::Local,
        definition: SymbolDefinition::Label {
            section: SectionId(0),
            offset: 1,
        },
        source: None,
    });
    let mut provider = fixture();
    provider.sections[0].name = "provider".into();
    provider.sections[0].address = Some(0x200);
    provider.sections[0].data.fill(0);
    provider.symbols[0].name = "Provider".into();
    provider.symbols.push(Symbol {
        name: "Start".into(),
        binding: SymbolBinding::Local,
        definition: SymbolDefinition::Import,
        source: None,
    });
    provider.symbols.push(consumer.symbols[1].clone());
    provider.sections[0].patches.push(Patch {
        offset: 0,
        width: PatchWidth::Word,
        semantics: PatchSemantics::Absolute,
        pc: AddressContext {
            section: SectionId(0),
            offset: 0,
        },
        expression: RelocationExpr::Binary(
            BinaryOp::Add,
            Box::new(RelocationExpr::Symbol(SymbolId(1))),
            Box::new(RelocationExpr::Literal(1)),
        ),
        source: Provenance {
            file: "input.asm".into(),
            line: 1,
            column: 1,
            expansion: vec![],
        },
    });
    let runs = [dir.join("first"), dir.join("second")];
    for run in &runs {
        std::fs::create_dir_all(run).unwrap();
        for (name, object) in [("consumer.o", &consumer), ("provider.o", &provider)] {
            let mut child = Command::new(env!("CARGO_BIN_EXE_koh-link"))
                .args(["--emit-elf", "-o"])
                .arg(run.join(name))
                .stdin(Stdio::piped())
                .stderr(Stdio::piped())
                .spawn()
                .unwrap();
            child
                .stdin
                .take()
                .unwrap()
                .write_all(&serde_json::to_vec(object).unwrap())
                .unwrap();
            let output = child.wait_with_output().unwrap();
            assert!(
                output.status.success(),
                "{}",
                String::from_utf8_lossy(&output.stderr)
            );
        }
        let output = Command::new(env!("CARGO_BIN_EXE_koh-link"))
            .arg("-o")
            .arg(run.join("image.gb"))
            .arg("-n")
            .arg(run.join("image.sym"))
            .arg("-d")
            .arg(run.join("image.elf"))
            .arg("--report-json")
            .arg(run.join("image.json"))
            .arg(run.join("consumer.o"))
            .arg(run.join("provider.o"))
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert_eq!(
            &std::fs::read(run.join("image.gb")).unwrap()[0x200..0x202],
            &[1, 1]
        );
    }
    for artifact in [
        "consumer.o",
        "provider.o",
        "image.gb",
        "image.sym",
        "image.elf",
        "image.json",
    ] {
        let first = std::fs::read(runs[0].join(artifact)).unwrap();
        let second = std::fs::read(runs[1].join(artifact)).unwrap();
        assert!(!first.is_empty(), "{artifact}");
        assert_eq!(first, second, "{artifact} differs across processes");
    }
    std::fs::remove_dir_all(dir).unwrap();
}
