use koh_lsp::worker::Worker;
use serde_json::json;

#[test]
fn worker_include_discovery_preserves_managed_syntax_cases() {
    let mut worker = Worker::assembler().unwrap();
    for (text, expected) in [
        ("INCLUDE \"utils.asm\"", vec!["utils.asm"]),
        (
            "INCLUDE \"header.asm\"\nnop\nINCLUDE \"footer.asm\"",
            vec!["header.asm", "footer.asm"],
        ),
        (
            "include \"lower.asm\"\nInclude \"mixed.asm\"\nINCLUDE \"upper.asm\"",
            vec!["lower.asm", "mixed.asm", "upper.asm"],
        ),
        (
            "; INCLUDE \"commented.asm\"\nINCLUDE \"real.asm\"",
            vec!["real.asm"],
        ),
        ("INCLUDE \"utils.asm\" ; utilities", vec!["utils.asm"]),
        ("main: INCLUDE \"utils.asm\"", vec!["utils.asm"]),
        ("INCLUDE \"broken\nINCLUDE \"valid.asm\"", vec!["valid.asm"]),
        ("INCLUDE broken.asm", vec![]),
        ("", vec![]),
        ("INCLUDE \"helpers/math.asm\"", vec!["helpers/math.asm"]),
        (
            "/* INCLUDE \"blocked.asm\" */\nINCLUDE \"real.asm\"",
            vec!["real.asm"],
        ),
        (
            "/*\nINCLUDE \"blocked.asm\"\n*/\nINCLUDE \"real.asm\"",
            vec!["real.asm"],
        ),
        ("INCLUDE \"overlay-only.asm\"", vec!["overlay-only.asm"]),
        ("xINCLUDE \"not_real.asm\"", vec![]),
        ("    \t INCLUDE \"indented.asm\"", vec!["indented.asm"]),
        ("INCLUDE \"\"", vec![]),
    ] {
        let response=worker.request(&json!({"version":1,"op":"parse","sources":[{"path":"/main.asm","text":text}],"roots":["/main.asm"]})).unwrap();
        let includes: Vec<_> = response["documents"][0]["includes"]
            .as_array()
            .unwrap()
            .iter()
            .filter_map(|i| i["path"].as_str())
            .collect();
        assert_eq!(includes, expected, "{text}");
    }
}
