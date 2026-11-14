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
    let included = included.canonicalize().unwrap();
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
