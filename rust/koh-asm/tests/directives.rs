use koh_asm::assemble;

#[test]
fn immutable_constants_require_explicit_redefinition() {
    for source in ["DEF X EQU 1\nDEF X = 2", "DEF X EQU 1\nDEF X EQU 1"] {
        assert!(assemble(source, "constant.asm", false).is_err(), "{source}");
    }
    let object = assemble(
        "DEF X EQU 1\nREDEF X EQU 2\nSECTION \"code\",ROM0\ndb X",
        "constant.asm",
        false,
    )
    .unwrap();
    assert_eq!(object.sections[0].data, [2]);
}

#[test]
fn purge_allows_redefinition_after_fixed_references_are_resolved() {
    let object = assemble(
        "SECTION \"code\",ROM0[$100]\nLabel: nop\ndw Label\nPURGE Label\nLabel: db 2\ndw Label",
        "purge.asm",
        false,
    )
    .unwrap();
    assert_eq!(object.sections[0].data, [0, 0, 1, 2, 3, 1]);
    assert_eq!(object.symbols.len(), 1);
    assert!(object.sections[0].patches.is_empty());
}

#[test]
fn purge_rejects_undefined_and_unresolved_referenced_symbols() {
    for source in [
        "PURGE Missing",
        "DEF N EQU 1\nPURGE N\nPURGE N",
        "SECTION \"code\",ROM0\nLabel: dw Label\nPURGE Label",
    ] {
        assert!(assemble(source, "purge.asm", true).is_err(), "{source}");
    }
}

#[test]
fn local_labels_may_omit_colon() {
    let obj = assemble(
        "SECTION \"code\",ROM0[$100]\nMain:\n.loop\nnop\njr .loop",
        "local.asm",
        false,
    )
    .unwrap();
    assert_eq!(obj.sections[0].data, [0, 0x18, 0xfd]);
}

#[test]
fn assertions_resolve_same_section_differences_without_placement() {
    let object = assemble(
        "SECTION \"ram\",WRAM0\nFirst: db\nSecond: db\nASSERT Second-First==1",
        "assert.asm",
        false,
    )
    .unwrap();
    assert_eq!(object.sections[0].size, 2);
}

#[test]
fn ds_fill_evaluates_current_address_for_each_byte() {
    let obj = assemble("SECTION \"code\",ROM0[0]\nds 4,@", "fill.asm", false).unwrap();
    assert_eq!(obj.sections[0].data, [0, 1, 2, 3]);
}

#[test]
fn ds_alignment_is_a_count_expression() {
    let obj = assemble(
        "SECTION \"code\",ROM0[$100]\ndb 1,2,3\nds ALIGN[3],$aa\ndb 4",
        "align.asm",
        false,
    )
    .unwrap();
    assert_eq!(
        obj.sections[0].data,
        [1, 2, 3, 0xaa, 0xaa, 0xaa, 0xaa, 0xaa, 4]
    );
}

#[test]
fn character_literals_follow_active_map_and_map_stack() {
    let obj=assemble("CHARMAP \"A\",1\nPRECHMAP\nNEWCHARMAP alt\nCHARMAP \"A\",2\nPOPCHARMAP\nDEF CHAR EQU 'A'\nSECTION \"code\",ROM0[0]\nld a,'A'\ndb CHAR\nSETCHARMAP \"\"\ndb \"A\"","map.asm",false).unwrap();
    assert_eq!(obj.sections[0].data, [0x3e, 1, 1, 1]);
}
