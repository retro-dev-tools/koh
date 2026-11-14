use koh_asm::assemble;

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
