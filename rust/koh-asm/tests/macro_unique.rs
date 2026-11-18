use koh_asm::assemble;

const WAIT: &str = r"WAIT_VBLANK: MACRO
.wait\@:
    ldh a, [$FF80]
    and a
    jr z, .wait\@
ENDM
";

fn sample_macros() -> String {
    format!(
        "DEF TILE_FONT_SPACE EQU $20\n{WAIT}{}\n",
        include_str!("../../../samples/asm/gb-2048/src/macros.inc")
    )
}

#[test]
fn unique_wait_labels_preserve_subsequent_sample_macros() {
    let source = sample_macros()
        + "SECTION \"Main\", ROM0[$100]\nMain:\nWAIT_VBLANK\nWAIT_VBLANK\nTILE_ROW $0F,$F0\nFILL_BYTES 2,$A5\nFILL4_HL $42\nFILL4_HL_A\nGRID_CELLROW 1,2\nfarcall 1,FarcallTrampoline\nFarcallTrampoline: ret\n";
    let object = assemble(&source, "macro-unique.asm", false).unwrap();
    let mut expected = vec![
        0xf0, 0x80, 0xa7, 0x28, 0xfb, 0xf0, 0x80, 0xa7, 0x28, 0xfb, 0xf0, 0x0f, 0xa5, 0xa5, 0x3e,
        0x42,
    ];
    expected.extend([0x22; 8]);
    expected.extend([0x20; 2]);
    expected.extend([1, 1, 1, 2].repeat(4));
    expected.extend([0x20; 14]);
    expected.extend([0x3e, 1, 1, 0x40, 1, 0xcd, 0x40, 1, 0xc9]);
    assert_eq!(object.sections[0].data, expected);
}

#[test]
fn unique_wait_labels_survive_nested_macro_repetition() {
    let source = sample_macros()
        + r"WAIT_TWICE: MACRO
 REPT 2
  WAIT_VBLANK
 ENDR
 TILE_ROW \1,\2
ENDM
SECTION "
        + "\"Main\", ROM0[$100]\nMain:\nWAIT_TWICE $0F,$F0\nWAIT_TWICE $AA,$55\n";
    let object = assemble(&source, "nested-unique.asm", false).unwrap();
    let wait = [0xf0, 0x80, 0xa7, 0x28, 0xfb];
    let mut expected = wait.repeat(2);
    expected.extend([0xf0, 0x0f]);
    expected.extend(wait.repeat(2));
    expected.extend([0x55, 0xaa]);
    assert_eq!(object.sections[0].data, expected);
}

#[test]
fn macro_patch_provenance_tracks_repetition_and_does_not_leak_after_break() {
    let source = "MACRO jump_import\n    jp External\n    BREAK\nENDM\nSECTION \"Main\", ROM0\nREPT 2\n  jump_import\nENDR\njp OtherImport\n";
    let object = assemble(source, "macro-provenance.asm", true).unwrap();
    let patches = &object.sections[0].patches;
    assert_eq!(patches.len(), 2);
    let origin = &patches[0].source;
    assert_eq!(
        (origin.file.as_str(), origin.line, origin.column),
        ("macro-provenance.asm", 2, 5)
    );
    assert_eq!(
        origin
            .expansion
            .iter()
            .map(|s| (s.file.as_str(), s.line, s.column))
            .collect::<Vec<_>>(),
        [
            ("macro-provenance.asm", 6, 1),
            ("macro-provenance.asm", 7, 3)
        ]
    );
    assert_eq!(patches[1].source.line, 9);
    assert!(patches[1].source.expansion.is_empty());
}

#[test]
fn macro_patch_provenance_tracks_include_for_and_call() {
    struct Fixture(std::path::PathBuf);
    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let fixture = Fixture(
        std::env::temp_dir().join(format!("koh-include-provenance-{}", std::process::id())),
    );
    std::fs::create_dir(&fixture.0).unwrap();
    let included = fixture.0.join("body.inc");
    std::fs::write(&included, "MACRO call_external\n    call External\nENDM\nSECTION \"Main\", ROM0\nFOR i,0,1\n  call_external\nENDR\n").unwrap();
    let main = fixture.0.join("main.asm");
    let file = main.to_str().unwrap();
    let source = "INCLUDE \"body.inc\"\njp AfterImport\n";
    std::fs::write(&main, source).unwrap();
    let object = assemble(source, file, true).unwrap();
    let patches = &object.sections[0].patches;
    assert_eq!(patches.len(), 2);
    let origin = &patches[0].source;
    assert_eq!(
        std::fs::canonicalize(&origin.file).unwrap(),
        included.canonicalize().unwrap()
    );
    let included = included.to_str().unwrap();
    assert_eq!(
        (origin.file.as_str(), origin.line, origin.column),
        (included, 2, 5)
    );
    assert_eq!(
        origin
            .expansion
            .iter()
            .map(|s| (s.file.as_str(), s.line, s.column))
            .collect::<Vec<_>>(),
        [(file, 1, 1), (included, 5, 1), (included, 6, 3)]
    );
    assert_eq!(patches[1].source.file, file);
    assert!(patches[1].source.expansion.is_empty());
}

#[cfg(unix)]
#[test]
fn include_provenance_preserves_the_logical_symlink_path() {
    struct Fixture(std::path::PathBuf);
    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let directory =
        std::env::temp_dir().join(format!("koh-symlink-provenance-{}", std::process::id()));
    std::fs::create_dir(&directory).unwrap();
    let fixture = Fixture(directory);
    let real = fixture.0.join("real");
    let alias = fixture.0.join("alias");
    std::fs::create_dir(&real).unwrap();
    std::os::unix::fs::symlink(&real, &alias).unwrap();
    std::fs::write(
        real.join("body.inc"),
        "SECTION \"code\",ROM0\ncall External",
    )
    .unwrap();
    let main = alias.join("main.asm");
    let object = assemble("INCLUDE \"body.inc\"", main.to_str().unwrap(), true).unwrap();
    let source = &object.sections[0].patches[0].source;
    assert_eq!(source.file, alias.join("body.inc").to_str().unwrap());
    assert_eq!(
        std::fs::canonicalize(&source.file).unwrap(),
        real.join("body.inc").canonicalize().unwrap()
    );
    assert_eq!((source.line, source.column), (2, 1));
    assert_eq!(source.expansion[0].file, main.to_str().unwrap());
}

#[test]
fn constant_instruction_line_maps_keep_nested_macro_calls_through_elf() {
    let source = "MACRO inner\n nop\nENDM\nMACRO outer\n inner\nENDM\nSECTION \"code\",ROM0[$100]\nouter\nnop\n";
    let object = assemble(source, "nested-lines.asm", false).unwrap();
    assert!(object.sections[0].patches.is_empty());
    let restored =
        koh_asm::object::read_elf(&koh_asm::object::write_elf(&object).unwrap()).unwrap();
    let lines = &restored.sections[0].lines;
    assert_eq!(lines.len(), 2);
    assert_eq!(lines[0].line, 2);
    assert_eq!(
        lines[0]
            .expansion
            .iter()
            .map(|s| (s.file.as_str(), s.line, s.column))
            .collect::<Vec<_>>(),
        [("nested-lines.asm", 8, 1), ("nested-lines.asm", 5, 2)]
    );
    assert!(lines[1].expansion.is_empty());
    let linked = koh_link::link(&[restored], &Default::default()).unwrap();
    assert_eq!(linked.lines[0].expansion.len(), 2);
    assert!(linked.lines[1].expansion.is_empty());
}

#[test]
fn load_debug_lines_use_execution_addresses_and_keep_macro_expansions() {
    let source = "MACRO inner\n nop\n jr @\nENDM\nMACRO outer\n inner\nENDM\nSECTION \"storage\",ROM0[$100]\nLOAD \"Fast\",HRAM[$ff80]\nouter\nENDL\nnop\n";
    let model = assemble(source, "load-debug.asm", false).unwrap();
    assert_eq!(model.sections[0].data, [0, 0x18, 0xfe, 0]);
    assert_eq!(model.sections[0].lines.len(), 1);
    assert_eq!(model.sections[0].lines[0].offset, 3);
    assert_eq!(
        model.sections[1]
            .lines
            .iter()
            .map(|line| line.offset)
            .collect::<Vec<_>>(),
        [0, 1]
    );
    let restored = koh_asm::object::read_elf(&koh_asm::object::write_elf(&model).unwrap()).unwrap();
    let linked = koh_link::link(&[restored], &Default::default()).unwrap();
    assert_eq!(
        linked
            .lines
            .iter()
            .map(|line| line.address)
            .collect::<Vec<_>>(),
        [0x103, 0xff80, 0xff81]
    );
    assert!(linked.lines[0].expansion.is_empty());
    for line in &linked.lines[1..] {
        assert_eq!(
            line.expansion
                .iter()
                .map(|frame| frame.line)
                .collect::<Vec<_>>(),
            [10, 6]
        );
    }
    assert_eq!(linked.lines[2].count, 2);
    let debug = linked.dwarf().unwrap();
    let u32_at =
        |offset| u32::from_le_bytes(debug[offset..offset + 4].try_into().unwrap()) as usize;
    let map = u32_at(24);
    assert_eq!(u32_at(map), 3);
    let addresses: Vec<_> = (0..3)
        .map(|index| {
            let offset = map + 4 + index * 16;
            u16::from_le_bytes(debug[offset + 2..offset + 4].try_into().unwrap())
        })
        .collect();
    assert_eq!(addresses, [0x103, 0xff80, 0xff81]);
    assert_eq!(u32_at(map + 4 + 12), u32::MAX as usize);
    for index in 1..3 {
        let expansion = u32_at(map + 4 + index * 16 + 12);
        assert_eq!(
            u16::from_le_bytes(debug[expansion..expansion + 2].try_into().unwrap()),
            2
        );
    }
}
