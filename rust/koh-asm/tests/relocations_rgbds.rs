use koh_asm::{assemble, model::*, object::write_elf};
use std::{
    fs,
    path::PathBuf,
    process::{Command, Output},
    sync::atomic::{AtomicU64, Ordering},
};

static NEXT: AtomicU64 = AtomicU64::new(0);

struct Fixture(PathBuf);

impl Fixture {
    fn new(name: &str) -> Option<Self> {
        if std::env::var("KOH_RGBDS_TEST").as_deref() != Ok("1") {
            return None;
        }
        for tool in ["rgbasm", "rgblink"] {
            let output = Command::new(tool).arg("--version").output().unwrap();
            assert!(output.status.success());
            assert_eq!(
                String::from_utf8_lossy(&output.stdout).trim(),
                format!("{tool} v1.0.1")
            );
        }
        let path = std::env::temp_dir().join(format!(
            "koh-relocations-rgbds-{}-{name}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        println!("Relocation artifacts: {}", path.display());
        Some(Self(path))
    }

    fn command(&self, name: &str, tool: &str, args: &[String]) -> Output {
        let output = Command::new(tool).args(args).output().unwrap();
        fs::write(
            self.0.join(format!("{name}.log")),
            format!(
                "COMMAND: {tool} {args:?}\nEXIT: {:?}\nSTDOUT:\n{}\nSTDERR:\n{}",
                output.status.code(),
                String::from_utf8_lossy(&output.stdout),
                String::from_utf8_lossy(&output.stderr)
            ),
        )
        .unwrap();
        output
    }

    fn object(&self, name: &str, source: &str, native: bool) -> (PathBuf, Option<Object>) {
        let asm = self.0.join(format!("{name}.asm"));
        let output = self.0.join(format!("{name}.o"));
        fs::write(&asm, source).unwrap();
        let model = assemble(source, asm.to_str().unwrap(), true).unwrap();
        fs::write(output.with_extension("elf.o"), write_elf(&model).unwrap()).unwrap();
        if native {
            let reference = output.with_extension("reference.o");
            let check = self.command(
                &format!("{name}-size-object"),
                "rgbasm",
                &[
                    "-o".into(),
                    reference.to_string_lossy().into_owned(),
                    asm.to_string_lossy().into_owned(),
                ],
            );
            assert!(
                check.status.success(),
                "{}",
                String::from_utf8_lossy(&check.stderr)
            );
            let object = assemble(source, asm.to_str().unwrap(), true).unwrap();
            fs::write(&output, write_elf(&object).unwrap()).unwrap();
            (output, Some(object))
        } else {
            let result = self.command(
                name,
                "rgbasm",
                &[
                    "-o".into(),
                    output.to_string_lossy().into_owned(),
                    asm.to_string_lossy().into_owned(),
                ],
            );
            assert!(
                result.status.success(),
                "{}",
                String::from_utf8_lossy(&result.stderr)
            );
            (output, None)
        }
    }

    fn link(&self, name: &str, objects: &[PathBuf]) -> (Output, PathBuf) {
        let rom = self.0.join(format!("{name}.gb"));
        if fs::read(&objects[0]).unwrap().starts_with(b"\x7fELF") {
            #[cfg(unix)]
            use std::os::unix::process::ExitStatusExt;
            #[cfg(windows)]
            use std::os::windows::process::ExitStatusExt;
            let models: Vec<_> = objects
                .iter()
                .map(|p| {
                    koh_asm::object::read_elf(
                        &fs::read(if fs::read(p).unwrap().starts_with(b"\x7fELF") {
                            p.clone()
                        } else {
                            p.with_extension("elf.o")
                        })
                        .unwrap(),
                    )
                    .unwrap()
                })
                .collect();
            let size_rom = self.0.join(format!("{name}-size.gb"));
            let mut size_args = vec!["-o".into(), size_rom.to_string_lossy().into_owned()];
            size_args.extend(
                objects
                    .iter()
                    .map(|p| {
                        if fs::read(p).unwrap().starts_with(b"\x7fELF") {
                            p.with_extension("reference.o")
                        } else {
                            p.clone()
                        }
                    })
                    .map(|p| p.to_string_lossy().into_owned()),
            );
            let reference = self.command(&format!("{name}-size"), "rgblink", &size_args);
            let size = if reference.status.success() {
                fs::metadata(&size_rom).unwrap().len() as usize
            } else {
                koh_link::MAX_ROM_SIZE
            };
            let result = koh_link::link(
                &models,
                &koh_link::LinkOptions {
                    raw: true,
                    size: Some(size),
                    ..Default::default()
                },
            );
            let (status, stderr) = match result {
                Ok(result) => {
                    fs::write(&rom, result.rom).unwrap();
                    (0, Vec::new())
                }
                Err(error) => (1, error.into_bytes()),
            };
            return (
                Output {
                    status: std::process::ExitStatus::from_raw(status),
                    stdout: Vec::new(),
                    stderr,
                },
                rom,
            );
        }

        let mut args = vec!["-o".to_owned(), rom.to_string_lossy().into_owned()];
        args.extend(objects.iter().map(|p| p.to_string_lossy().into_owned()));
        (self.command(name, "rgblink", &args), rom)
    }

    fn linked(&self, name: &str, objects: &[PathBuf]) -> Vec<u8> {
        let (output, rom) = self.link(name, objects);
        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
        fs::read(rom).unwrap()
    }
}

#[test]
fn complete_relocation_expressions_link_independently() {
    let Some(f) = Fixture::new("expressions") else {
        return;
    };
    let consumer = "SECTION \"code\",ROM0[$100]\ndb HIGH(Target)+1\ndw LOW(Target)\ndb 2 ** (Target >> 14)\ndl (Target-$4001) >>> 1\n";
    let provider = "SECTION \"target\",ROMX,BANK[2]\nTarget:: db $a5\n";
    let (native, object) = f.object("native", consumer, true);
    let object = object.unwrap();
    let patches = &object.sections[0].patches;
    assert_eq!(patches.len(), 4);
    assert!(matches!(
        patches[0].expression,
        RelocationExpr::Binary(BinaryOp::Add, _, _)
    ));
    assert_eq!(patches[1].width, PatchWidth::Word);
    assert!(matches!(
        patches[2].expression,
        RelocationExpr::Binary(BinaryOp::Power, _, _)
    ));
    assert!(matches!(
        patches[3].expression,
        RelocationExpr::Binary(BinaryOp::UnsignedShiftRight, _, _)
    ));
    let (reference, _) = f.object("reference", consumer, false);
    let (provider, _) = f.object("provider", provider, false);
    let actual = f.linked("native-link", &[native, provider.clone()]);
    let expected = f.linked("reference-link", &[reference, provider]);
    assert_eq!(actual, expected);
    assert_eq!(
        &actual[0x100..0x108],
        &[0x41, 0, 0, 2, 0xff, 0xff, 0xff, 0x7f]
    );
    assert_eq!(actual[0x8000], 0xa5);
}

#[test]
fn load_uses_floating_execution_section_for_pc_bank_and_jr() {
    let Some(f) = Fixture::new("load") else {
        return;
    };
    let source = "SECTION \"storage\",ROM0[$100]\nLOAD \"runtime\",WRAM0\nStart:\ndw @\ndb BANK(@)\njr Next\ndb $aa\nNext:\njr Start\nENDL\n";
    let (native, object) = f.object("native", source, true);
    let object = object.unwrap();
    let patch = &object.sections[0].patches[0];
    assert_eq!(patch.pc.section, SectionId(1));
    assert_eq!(patch.pc.offset, 0);
    assert_eq!(patch.expression, RelocationExpr::CurrentAddress);
    assert_eq!(object.sections[1].size, 8);
    assert!(object.sections[1].data.is_empty());
    let (reference, _) = f.object("reference", source, false);
    let actual = f.linked("native-link", &[native]);
    assert_eq!(actual, f.linked("reference-link", &[reference]));
    assert_eq!(
        &actual[0x100..0x108],
        &[0, 0xc0, 0, 0x18, 1, 0xaa, 0x18, 0xf8]
    );
}

#[test]
fn signed_external_offset_is_asserted_at_link_time() {
    let Some(f) = Fixture::new("signed") else {
        return;
    };
    let consumer = "SECTION \"code\",ROM0[$100]\nadd sp,Delta\nld hl,sp+Delta\n";
    let (native, object) = f.object("native", consumer, true);
    assert!(
        object.unwrap().sections[0]
            .patches
            .iter()
            .all(|p| p.semantics == PatchSemantics::Signed)
    );
    let (reference, _) = f.object("reference", consumer, false);
    let (valid, _) = f.object("valid", "DEF Delta EQU 127\nEXPORT Delta\n", false);
    let actual = f.linked("valid-native-link", &[native.clone(), valid.clone()]);
    assert_eq!(
        actual,
        f.linked("valid-reference-link", &[reference, valid])
    );
    assert_eq!(&actual[0x100..0x104], &[0xe8, 127, 0xf8, 127]);
    let (invalid, _) = f.object("invalid", "DEF Delta EQU 128\nEXPORT Delta\n", false);
    let (output, _) = f.link("invalid-native-link", &[native, invalid]);
    assert!(!output.status.success());
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(
        stderr.contains("Signed relocation is out of range")
            || stderr.contains("Signed relocation out of range")
            || (stderr.contains("128") && stderr.contains("8-bit range")),
        "{stderr}"
    );
    println!("Expected signed failure: {stderr}");
}

#[test]
fn external_high_memory_address_is_validated_at_link_time() {
    let Some(f) = Fixture::new("high-memory") else {
        return;
    };
    let consumer = "SECTION \"code\",ROM0[$100]\nldh a,[Address]\nldh [Address],a\n";
    let (native, object) = f.object("native", consumer, true);
    assert!(
        object.unwrap().sections[0]
            .patches
            .iter()
            .all(|p| p.semantics == PatchSemantics::HighMemory)
    );
    let (reference, _) = f.object("reference", consumer, false);
    let (valid, _) = f.object("valid", "DEF Address EQU $ff80\nEXPORT Address\n", false);
    let actual = f.linked("valid-native-link", &[native.clone(), valid.clone()]);
    assert_eq!(
        actual,
        f.linked("valid-reference-link", &[reference.clone(), valid])
    );
    assert_eq!(&actual[0x100..0x104], &[0xf0, 0x80, 0xe0, 0x80]);
    let (invalid, _) = f.object("invalid", "DEF Address EQU $1234\nEXPORT Address\n", false);
    for (name, object) in [("native", native), ("reference", reference)] {
        let (output, _) = f.link(&format!("invalid-{name}-link"), &[object, invalid.clone()]);
        assert!(!output.status.success());
        let stderr = String::from_utf8_lossy(&output.stderr);
        assert!(
            stderr.contains("HRAM")
                || stderr.to_ascii_lowercase().contains("high-memory")
                || stderr.contains("high memory"),
            "{stderr}"
        );
        println!("Expected high-memory failure ({name}): {stderr}");
    }
}

#[test]
fn unresolved_import_is_a_link_error() {
    let Some(f) = Fixture::new("missing-import") else {
        return;
    };
    let source = "SECTION \"code\",ROM0[$100]\njp Missing\n";
    let (native, object) = f.object("native", source, true);
    assert!(
        object
            .unwrap()
            .symbols
            .iter()
            .any(|s| s.name == "Missing" && s.definition == SymbolDefinition::Import)
    );
    let (reference, _) = f.object("reference", source, false);
    for (name, object) in [("native", native), ("reference", reference)] {
        let (output, _) = f.link(&format!("{name}-link"), &[object]);
        assert!(!output.status.success());
        let stderr = String::from_utf8_lossy(&output.stderr);
        assert!(
            stderr.contains("Undefined symbol") && stderr.contains("Missing"),
            "{stderr}"
        );
        println!("Expected missing-import failure ({name}): {stderr}");
    }
}

#[test]
fn floating_alignment_constrains_base_without_unnecessary_padding() {
    let Some(f) = Fixture::new("floating-alignment") else {
        return;
    };
    let source = "SECTION \"head\",ROM0[0]\nds 7\nSECTION \"tail\",ROM0[9]\nds $4000-9\nSECTION \"fit\",ROM0\ndb $11\nALIGN 3\ndb $22\n";
    let (reference, _) = f.object("reference", source, false);
    let expected = f.linked("reference-link", &[reference]);
    assert_eq!(&expected[7..9], &[0x11, 0x22]);
    let (native, object) = f.object("native", source, true);
    let object = object.unwrap();
    let fit = object.sections.iter().find(|s| s.name == "fit").unwrap();
    assert_eq!(fit.address, None);
    assert_eq!(fit.align, 3);
    assert_eq!(fit.align_offset, 7);
    assert_eq!(fit.size, 2);
    assert_eq!(fit.data, [0x11, 0x22]);
    assert_eq!(f.linked("native-link", &[native]), expected);
}
