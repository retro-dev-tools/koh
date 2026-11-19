use koh_lsp::{
    tokens::{TOKEN_TYPES, classify, encode, unclassified},
    worker::Worker,
};
use serde_json::json;

#[test]
fn semantic_categories_and_delta_encoding_match_the_existing_legend() {
    let mut worker = Worker::assembler().unwrap();
    for (source, kind) in [
        ("nop", 0),
        ("ld a,b", 9),
        ("SECTION \"Main\",ROM0", 11),
        ("ld a,$42", 2),
        ("db \"hello\"", 3),
        ("; comment", 4),
        ("Global:\n.local:", 6),
        ("X EQU 1+2", 5),
        ("jp nz,$1234", 10),
        ("Label: jp Label", 1),
        ("SECTION \"Main\",ROM0", 12),
        ("MyMacro: MACRO\nld a,\\1\nENDM", 8),
    ] {
        let response=worker.request(&json!({"version":1,"op":"parse","sources":[{"path":"tokens.asm","text":source}],"roots":["tokens.asm"]})).unwrap();
        let data = encode(source, &response["documents"][0]);
        assert!(
            data.as_chunks::<5>().0.iter().any(|token| token[3] == kind),
            "{source}: {data:?}"
        );
    }
    assert_eq!(
        TOKEN_TYPES,
        [
            "keyword",
            "variable",
            "number",
            "string",
            "comment",
            "operator",
            "label",
            "macro",
            "parameter",
            "register",
            "conditionFlag",
            "directive",
            "sectionType"
        ]
    );
    for (source, expected) in [
        ("nop\nhalt", vec![0, 0, 3, 0, 0, 1, 0, 4, 0, 0]),
        ("", vec![]),
    ] {
        let response = worker
            .request(
                &json!({"version":1,"op":"parse","sources":[{"path":"tokens.asm","text":source}]}),
            )
            .unwrap();
        assert_eq!(encode(source, &response["documents"][0]), expected);
    }
}

#[test]
fn multiline_comments_split_at_crlf_boundaries_in_utf16_units() {
    let source = "/*😀\r\nline\r\n*/ nop";
    let mut worker = Worker::assembler().unwrap();
    let response = worker
        .request(&json!({"version":1,"op":"parse","sources":[{"path":"tokens.asm","text":source}]}))
        .unwrap();
    assert_eq!(
        encode(source, &response["documents"][0]),
        vec![0, 0, 4, 4, 0, 1, 0, 4, 4, 0, 1, 0, 2, 4, 0, 0, 3, 3, 0, 0]
    );
}

#[test]
fn every_baseline_syntax_kind_is_classified_or_explicitly_excluded() {
    for kind in include_str!("fixtures/syntax-kinds.txt").lines() {
        assert_ne!(classify(kind).is_some(), unclassified(kind), "{kind}");
    }
    assert!(classify("UnknownKind").is_none());
    assert!(!unclassified("UnknownKind"));
}

#[test]
fn worker_kinds_remain_within_the_classification_contract() {
    let source = include_str!("../../koh-asm/src/analysis.rs");
    for kind in source.split('"').skip(1).step_by(2) {
        if kind.chars().all(|c| c.is_ascii_alphanumeric())
            && [
                "Keyword",
                "Token",
                "Trivia",
                "Operand",
                "Expression",
                "Directive",
            ]
            .iter()
            .any(|suffix| kind.len() > suffix.len() && kind.ends_with(suffix))
        {
            assert!(
                classify(kind).is_some() || unclassified(kind),
                "new worker kind: {kind}"
            );
        }
    }
    let words = source
        .split("const WORDS: &str = \"")
        .nth(1)
        .unwrap()
        .split('"')
        .next()
        .unwrap();
    let text = words.split_whitespace().collect::<Vec<_>>().join("\n");
    let mut worker = Worker::assembler().unwrap();
    let result = worker
        .request(
            &json!({"version":1,"op":"parse","sources":[{"path":"all-keywords.asm","text":text}]}),
        )
        .unwrap();
    for item in result["documents"][0]["tokens"]
        .as_array()
        .unwrap()
        .iter()
        .chain(result["documents"][0]["nodes"].as_array().unwrap())
    {
        let kind = item["kind"].as_str().unwrap();
        assert!(
            classify(kind).is_some() || unclassified(kind),
            "emitted {kind}"
        );
    }
}
