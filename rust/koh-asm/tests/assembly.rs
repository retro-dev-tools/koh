use koh_asm::assemble;

fn bytes(source: &str) -> Vec<u8> {
    assemble(source, "test.asm", false).unwrap().sections[0]
        .data
        .clone()
}

#[test]
fn forward_local_labels_and_current_address() {
    assert_eq!(
        bytes("SECTION \"x\", ROM0[$100]\nStart: jr .end\ndw @\n.end: ret"),
        [0x18, 2, 2, 1, 0xc9]
    );
}

#[test]
fn nested_macros_loops_and_mutable_constants() {
    assert_eq!(
        bytes(
            r#"SECTION "x", ROM0
MACRO pair
 db \1, \2
ENDM
DEF n = 0
REPT 3
 pair n, n+1
 REDEF n = n+2
ENDR
FOR i, 3, 0, -1
 db i
ENDR"#
        ),
        [0, 1, 2, 3, 4, 5, 3, 2, 1]
    );
}

#[test]
fn conditional_suppresses_invalid_source() {
    assert_eq!(
        bytes("SECTION \"x\", ROM0\nIF 0\ngarbage invalid\nELIF 1\ndb 7\nELSE\ndb 9\nENDC"),
        [7]
    );
}

#[test]
fn invalid_source_is_diagnostic() {
    for source in [
        "nop",
        "SECTION \"x\", ROM0\nwat",
        "IF 1",
        "MACRO x\nx\nENDM\nx",
        "SECTION \"x\", ROM0[0]\njr 1000",
        "SECTION \"x\", ROM0\nfoo:\nfoo:",
    ] {
        assert!(assemble(source, "bad.asm", false).is_err(), "{source}");
    }
}

#[test]
fn floating_symbol_relocation_survives() {
    let obj = assemble("SECTION \"x\", ROM0\nStart: jp Start", "test.asm", false).unwrap();
    assert_eq!(obj.sections[0].patches.len(), 1);
    let koh_asm::model::RelocationExpr::Symbol(id) = obj.sections[0].patches[0].expression else {
        panic!("missing symbolic expression")
    };
    assert_eq!(obj.symbols[id.0].name, "Start");
}

#[test]
fn independent_constant_fixture() {
    assert_eq!(
        bytes("DEF V EQU $1234\nSECTION \"x\", ROM0\nld hl,V\ndb HIGH(V),LOW(V)\ndw 1+2*3\n"),
        [0x21, 0x34, 0x12, 0x12, 0x34, 7, 0]
    );
}

#[test]
fn metadata_strings_and_floating_current_address() {
    let obj=assemble("DEF X EQU 3\nASSERT DEF(X)\nSECTION \"x\", ROM0\nStart: dw @\ndb STRLEN(STRCAT(\"a\",\"bc\"))", "test.asm",false).unwrap();
    assert_eq!(obj.sections[0].data[2], 3);
    assert_eq!(obj.sections[0].patches.len(), 1);
}

#[test]
fn same_section_difference_and_anonymous_jumps() {
    assert_eq!(
        bytes("SECTION \"x\", ROM0\nA: nop\nB: dw B-A\n: jr :-\njr :+\n: ret"),
        [0, 1, 0, 0x18, 0xfe, 0x18, 0, 0xc9]
    );
}
