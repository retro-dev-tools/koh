use koh_asm::{assemble, assemble_report, assemble_with_overlays};

fn bytes(body: &str) -> Vec<u8> {
    assemble(
        &format!("SECTION \"comments\", ROM0\n{body}"),
        "comments.asm",
        false,
    )
    .unwrap()
    .sections[0]
        .data
        .clone()
}

#[test]
fn block_comments_separate_tokens_and_keep_expressions() {
    assert_eq!(
        bytes("nop /* trailing */\ndb 1/* gap */+2\nret"),
        [0, 3, 0xc9]
    );
    assert!(assemble("SECTION \"x\", ROM0\nd/* gap */b 1", "fusion.asm", false).is_err());
}

#[test]
fn leading_comments_preserve_patch_source_line_and_column() {
    for (body, expected_line, expected_column) in [
        ("/* x */ call External", 2, 9),
        ("  /* λ */ call External", 2, 12),
        ("/* first\n*/ call External", 3, 4),
        (" /* first\n  second */  call External", 3, 14),
    ] {
        let object = assemble(
            &format!("SECTION \"x\", ROM0\n{body}"),
            "provenance.asm",
            true,
        )
        .unwrap();
        let source = &object.sections[0].patches[0].source;
        assert_eq!(
            (source.line, source.column),
            (expected_line, expected_column),
            "{body}"
        );
    }
}

#[test]
fn comment_content_does_not_start_literals_or_line_comments() {
    assert_eq!(
        bytes("db 1/* ; \" ' #\" λ */+2\r\nnop /**/\r\nret"),
        [3, 0, 0xc9]
    );
    assert_eq!(
        bytes("db \"escaped\\\"/*literal*/\"\nret"),
        b"escaped\"/*literal*/\xc9"
    );
}

#[test]
fn multiline_comments_keep_statement_and_physical_line_positions() {
    assert_eq!(bytes("db 1/* first\nsecond */+2\nret"), [3, 0xc9]);
    let errors = assemble(
        "SECTION \"x\", ROM0\nnop /* first\nsecond */\ninvalid",
        "lines.asm",
        false,
    )
    .unwrap_err();
    assert!(
        errors
            .iter()
            .any(|error| error.file == "lines.asm" && error.line == 4)
    );
    assert!(
        assemble(
            "SECTION \"x\", ROM0\nnop /*\n*/ ret",
            "same-statement.asm",
            false
        )
        .is_err()
    );
}

#[test]
fn comment_delimiters_in_literals_and_line_comments_are_data() {
    assert_eq!(
        bytes("db \"/* */\", '/', '*', #\"/* */\" ; /* ignored\nret"),
        b"/* *//*/* */\xc9"
    );
}

#[test]
fn block_comments_are_not_nested_and_warn_at_inner_opener() {
    let report = assemble_report(
        "SECTION \"x\", ROM0\nnop /* outer\n/* inner */\nret",
        "nested.asm",
        false,
    );
    assert_eq!(report.result.unwrap().sections[0].data, [0, 0xc9]);
    assert!(
        report
            .warnings
            .iter()
            .any(|warning| warning.line == 3 && warning.message.contains("Nested block comment"))
    );
    assert_eq!(bytes("nop /* /*/\nret"), [0, 0xc9]);
}

#[test]
fn unterminated_comments_are_errors_even_in_inactive_source() {
    for body in ["nop /* missing", "IF 0\n/* missing"] {
        let report = assemble_report(
            &format!("SECTION \"x\", ROM0\n{body}"),
            "unterminated.asm",
            false,
        );
        let errors = report.result.unwrap_err();
        assert!(errors.iter().any(|error| error.file == "unterminated.asm"
            && error.message.contains("Unterminated block comment")));
    }
}

#[test]
fn included_comment_errors_retain_include_location() {
    let errors = assemble_with_overlays(
        "INCLUDE \"inner.asm\"",
        "main.asm",
        false,
        &[("inner.asm".into(), "SECTION \"x\", ROM0\n/* missing".into())],
        &[],
    )
    .unwrap_err();
    assert!(errors.iter().any(|error| error.file.ends_with("inner.asm")
        && error.line == 2
        && error.message.contains("Unterminated block comment")));
}
