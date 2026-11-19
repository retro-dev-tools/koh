mod common;
use common::Fixture;
use koh_lsp::worker::Worker;
use serde_json::json;

#[test]
fn worker_preserves_instruction_bytes_virtual_dynamic_and_binary_overlays() {
    let mut worker = Worker::assembler().unwrap();
    let f = Fixture::new();
    let cases = [
        (
            "SECTION \"main\",ROM0[$100]\nStart: ld a,$12\nret",
            vec![],
            vec![],
            vec![0x3e, 0x12, 0xc9],
        ),
        (
            "INCLUDE \"defs.inc\"\nSECTION \"main\",ROM0\ndb VALUE",
            vec![("defs.inc", "DEF VALUE EQU 42")],
            vec![],
            vec![42],
        ),
        (
            "DEF FILE EQUS \"\\\"dynamic.inc\\\"\"\nINCLUDE FILE\nSECTION \"main\",ROM0\ndb VALUE",
            vec![("dynamic.inc", "DEF VALUE EQU 17")],
            vec![],
            vec![17],
        ),
        (
            "SECTION \"main\",ROM0\nINCBIN \"bytes.bin\"\nINCBIN \"text.txt\"",
            vec![("text.txt", "ok")],
            vec![
                ("bytes.bin", vec![0, 128, 255]),
                ("text.txt", vec![111, 107]),
            ],
            vec![0, 128, 255, 111, 107],
        ),
        (
            "SECTION \"main\",ROM0\nINCLUDE \"shared.inc\"\nINCBIN \"shared.inc\"",
            vec![("shared.inc", "db 7")],
            vec![("shared.inc", vec![8, 9])],
            vec![7, 8, 9],
        ),
    ];
    for (text, overlays, binary, expected) in cases {
        let main = f.0.join("main.asm");
        let mut sources = vec![json!({"path":main,"text":text})];
        sources.extend(
            overlays
                .into_iter()
                .map(|(name, text)| json!({"path":f.0.join(name),"text":text})),
        );
        let binary: Vec<_> = binary
            .into_iter()
            .map(|(name, data)| json!({"path":f.0.join(name),"data":data}))
            .collect();
        let response=worker.request(&json!({"version":1,"op":"analyze","sources":sources,"roots":[main],"searchRoots":[{"file":main,"roots":[f.0]}],"binaryFiles":binary})).unwrap();
        assert_eq!(response["ok"], true, "{text}: {response}");
        assert_eq!(
            response["object"]["sections"][0]["data"],
            json!(expected),
            "{text}"
        );
    }
}

#[test]
fn warnings_keep_utf16_spans_in_analysis_and_emission() {
    let mut worker = Worker::assembler().unwrap();
    let f = Fixture::new();
    let path = f.write("warnings.asm", "");
    let source = "; 😀\nWARN \"custom\"\nASSERT WARN,0,\"detail\"\nSECTION \"main\",ROM0\nnop";
    for op in ["analyze", "assemble"] {
        let response=worker.request(&json!({"version":1,"op":op,"sources":[{"path":path,"text":source}],"roots":[path]})).unwrap();
        assert_eq!(response["ok"], true);
        let warnings: Vec<_> = response["diagnostics"]
            .as_array()
            .unwrap()
            .iter()
            .filter(|d| d["severity"] == "Warning")
            .collect();
        assert_eq!(warnings.len(), 2);
        for (warning, marker, message) in [
            (warnings[0], "WARN", "custom"),
            (warnings[1], "ASSERT", "Assertion failed: detail"),
        ] {
            assert_eq!(warning["message"], message);
            assert_eq!(warning["path"], path.to_string_lossy().as_ref());
            assert_eq!(
                warning["start"],
                source[..source.find(marker).unwrap()]
                    .encode_utf16()
                    .count()
            );
        }
    }
}
