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

#[test]
fn exponential_equs_replay_has_a_byte_budget() {
    let errors = assemble(
        "DEF boom EQUS \"boom boom\"\nboom\n",
        "exponential.asm",
        false,
    )
    .unwrap_err();
    assert!(
        errors
            .iter()
            .any(|e| e.message.contains("expanded line exceeds 1 MiB"))
    );
}

#[test]
fn macro_and_interpolation_appends_have_a_byte_budget() {
    assert_eq!(
        bytes("MACRO emit\n db \\#\nENDM\nSECTION \"Main\",ROM0\nemit 1,2,3\n"),
        [1, 2, 3]
    );
    let large = "a".repeat(600_000);
    for source in [
        format!("MACRO emit\n db \\1,\\1\nENDM\nSECTION \"Main\",ROM0\nemit \"{large}\"\n"),
        format!("MACRO emit\n db \\#,\\#\nENDM\nSECTION \"Main\",ROM0\nemit \"{large}\"\n"),
        format!("DEF Large EQUS #\"{large}\"\nSECTION \"Main\",ROM0\ndb \"{{Large}}{{Large}}\"\n"),
    ] {
        let errors = assemble(&source, "expanded-append.asm", false).unwrap_err();
        assert!(
            errors
                .iter()
                .any(|e| e.message.contains("expanded line exceeds 1 MiB"))
        );
    }
}

#[test]
fn conditional_branch_order_is_validated_even_when_suppressed() {
    for body in [
        "IF 0\nELSE\nELSE\nENDC\n",
        "IF 0\nELSE\nELIF 1\nENDC\n",
        "IF 0\n IF 0\n ELSE\n ELSE\n ENDC\nENDC\n",
        "IF 0\n IF 0\n ELSE\n ELIF 1\n ENDC\nENDC\n",
    ] {
        let errors = assemble(
            &format!("SECTION \"Main\",ROM0\n{body}"),
            "bad-conditional.asm",
            false,
        )
        .unwrap_err();
        assert!(
            errors.iter().any(|e| e.message.contains("after ELSE")),
            "{errors:?}"
        );
    }
    assert_eq!(
        bytes(
            "SECTION \"Main\",ROM0\nIF 0\n db 1\nELIF 0\n db 2\nELIF 1\n db 3\nELSE\n db 4\nENDC\n"
        ),
        [3]
    );
}

#[test]
fn active_block_terminators_reject_trailing_tokens() {
    for body in [
        "IF 0\nELSE 1\nENDC\n",
        "IF 0\nENDC 1\n",
        "MACRO example\n nop\nENDM 1\n",
        "REPT 1\n nop\nENDR 1\n",
        "REPT 0\n nop\nENDR 1\n",
    ] {
        let errors = assemble(
            &format!("SECTION \"Main\",ROM0\n{body}"),
            "bad-terminator.asm",
            false,
        )
        .unwrap_err();
        assert!(
            errors
                .iter()
                .any(|e| e.message.contains("unexpected tokens after")),
            "{errors:?}"
        );
    }
    assert_eq!(
        bytes(
            "SECTION \"Main\",ROM0\nIF 0\n IF 0\n ELSE 1\n ENDC\n MACRO example\n  nop\n ENDM 1\nENDC\ndb 7\n"
        ),
        [7]
    );
}

#[test]
fn raw_macro_definitions_and_calls_use_the_same_name() {
    assert_eq!(
        bytes("MACRO #Example\n db 7\nENDM\nSECTION \"Main\",ROM0\nExample\n#Example\n"),
        [7, 7]
    );
    assert_eq!(
        bytes("MACRO Example\n db 8\nENDM\nSECTION \"Main\",ROM0\n#Example\n"),
        [8]
    );
}
