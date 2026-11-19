use koh_lsp::paths::{from_uri, normalize, offset, position, range, to_uri};
use serde_json::json;

#[test]
fn file_uris_round_trip_reserved_and_unicode_characters() {
    let path = std::env::temp_dir().join("koh # % żółć 😀.asm");
    let uri = to_uri(&path).unwrap();
    assert!(uri.contains("%23"));
    assert!(uri.contains("%25"));
    assert_eq!(from_uri(&uri).unwrap(), path);
    assert!(from_uri("https://example.com/a.asm").is_err());
    assert!(from_uri("file:///bad%ZZ.asm").is_err());
}

#[test]
fn normalization_does_not_require_the_file_to_exist() {
    let root = std::env::temp_dir();
    assert_eq!(
        normalize(&root.join("one/../two/./main.asm")).unwrap(),
        root.join("two/main.asm")
    );
}

#[test]
fn utf16_positions_and_crlf_boundaries_are_preserved() {
    let text = "; 😀\r\nLabel: nop\n";
    assert_eq!(offset(text, &json!({"line":0,"character":99})), 4);
    assert_eq!(offset(text, &json!({"line":1,"character":3})), 9);
    assert_eq!(
        offset(text, &json!({"line":99,"character":0})),
        text.encode_utf16().count()
    );
    assert_eq!(offset(text, &json!({"line":-1,"character":2})), 0);
    assert_eq!(position(text, 6), json!({"line":1,"character":0}));
    assert_eq!(
        range(text, 6, 5),
        json!({"start":{"line":1,"character":0},"end":{"line":1,"character":5}})
    );
    for n in [0, 1, 2, 4, 6, 9, 16, 17] {
        assert_eq!(offset(text, &position(text, n)), n);
    }
}
