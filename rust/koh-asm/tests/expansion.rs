use koh_asm::assemble;

fn bytes(source: &str) -> Vec<u8> {
    assemble(source, "expansion.asm", false).unwrap().sections[0]
        .data
        .clone()
}

#[test]
fn equs_redefinition_and_purge_targets_are_not_replayed() {
    assert_eq!(
        bytes("def X equs \"redef X equs \\\"nop\\\"\"\n{X}\nSECTION \"Main\",ROM0\n{X}\n"),
        [0]
    );
    assert_eq!(
        bytes(
            "def BYE equs \"PURGE BYE\\nWARN \\\"Crash?\\\"\\n\"\n{BYE}\nSECTION \"Main\",ROM0\nnop\n"
        ),
        [0]
    );
    assert_eq!(
        bytes(
            "SECTION \"Main\",ROM0\nDEF value EQUS \"1\"\nREDEF value EQUS \"2\"\ndb value\nPURGE value\nDEF value EQUS \"3\"\ndb value\n"
        ),
        [2, 3]
    );
}

#[test]
fn equs_multiline_replay_can_define_macro_and_control_blocks() {
    assert_eq!(
        bytes(
            "def DEFINE equs \"MACRO mac\\ndb $42\\nENDM\"\n{DEFINE}\nSECTION \"Main\",ROM0\nmac\n"
        ),
        [0x42]
    );
    assert_eq!(
        bytes("def BODY equs \"REPT 2\\ndb 7\\nENDR\"\nSECTION \"Main\",ROM0\n{BODY}\ndb 8\n"),
        [7, 7, 8]
    );
    assert_eq!(
        bytes("DEF First EQUS \"Second\"\nDEF Second EQUS \"nop\"\nSECTION \"Main\",ROM0\nFirst\n"),
        [0]
    );
}

#[test]
fn raw_strings_preserve_macro_escapes_and_interpolation() {
    assert_eq!(
        bytes("SECTION \"Main\",ROM0\ndb #\"\\1{Missing}\\@\"\n"),
        b"\\1{Missing}\\@"
    );
    assert_eq!(
        bytes("MACRO emit\n db #\"\\1\",\"\\1\"\nENDM\nSECTION \"Main\",ROM0\nemit A\n"),
        b"\\1A"
    );
    assert_eq!(
        bytes("SECTION \"Main\",ROM0\nassert !strcmp(#\"\\t\\1\",\"\\\\t\\\\1\")\nnop\n"),
        [0]
    );
}

#[test]
fn repeated_macro_redefinitions_and_unanchored_local_labels_work() {
    assert_eq!(
        bytes(
            "NEWCHARMAP mte\nCHARMAP \"One\",$80,$00\nCHARMAP \"Four\",$80,$01\nSETCHARMAP mte\nMACRO entry\n REDEF _TEXT EQUS REVCHAR(\\1,\\2)\n db STRLEN(\"{_TEXT}\")\nENDM\nSECTION \"Main\",ROM0\nentry $80,$00\nentry $80,$01\n"
        ),
        [3, 4]
    );
    assert_eq!(
        bytes("MACRO outer\n.label\\@:\n nop\nENDM\nSECTION \"Main\",ROM0\nouter\nouter\nouter\n"),
        [0, 0, 0]
    );
    assert_eq!(
        bytes("MACRO outer\n.local:\n jr .local\nENDM\nSECTION \"Main\",ROM0\nouter\nouter\n"),
        [0x18, 0xfe, 0x18, 0xfe]
    );
}

#[test]
fn recursive_multiline_replay_is_bounded() {
    let errors = assemble(
        "DEF RECUR EQUS \"RECUR\\nnop\"\nSECTION \"Main\",ROM0\nRECUR\n",
        "recursive.asm",
        false,
    )
    .unwrap_err();
    assert!(
        errors
            .iter()
            .any(|e| e.message.contains("string expansion depth exceeded"))
    );
}

#[test]
fn interpolation_uses_shared_numeric_formats_and_section_names() {
    assert_eq!(
        bytes(
            "DEF Value EQU 255\nSECTION \"Main\",ROM0[$100]\nLabel:\ndb \"{04d:Value}|{#X:Value}|{#b:Value}|{#o:Value}\"\ndb \"{SECTION(@)}|{SECTION(Label)}\"\n"
        ),
        b"0255|$FF|%11111111|&377Main|Main"
    );
    assert_eq!(bytes("SECTION \"Main\",ROM0[$100]\ndb \"{@}\"\n"), b"$100");
    for value in ["{z:Value}", "{04:Value}", "{SECTION(Value)}"] {
        let source = format!("DEF Value EQU 255\nSECTION \"Main\",ROM0\ndb \"{value}\"\n");
        assert!(
            assemble(&source, "bad-format.asm", false).is_err(),
            "{value}"
        );
    }
}
