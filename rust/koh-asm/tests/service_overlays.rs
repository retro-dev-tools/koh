use koh_asm::service::process;
use serde_json::{Value, json};

fn section_bytes(response: &Value) -> Vec<u8> {
    assert_eq!(response["ok"], true, "{response}");
    let bytes: Vec<u8> = serde_json::from_value(response["data"].clone()).unwrap();
    assert_eq!(&bytes[..6], b"KOH\0\x04\x01");
    assert_eq!(u16::from_le_bytes(bytes[6..8].try_into().unwrap()), 1);
    let mut offset = 8;
    let mut length = 0usize;
    let mut shift = 0;
    loop {
        let b = bytes[offset];
        offset += 1;
        length |= usize::from(b & 127) << shift;
        if b & 128 == 0 {
            break;
        }
        shift += 7;
    }
    offset += length + 1;
    for _ in 0..2 {
        let present = bytes[offset];
        offset += 1;
        if present != 0 {
            offset += 4;
        }
    }
    let size = u32::from_le_bytes(bytes[offset..offset + 4].try_into().unwrap()) as usize;
    bytes[offset + 4..offset + 4 + size].to_vec()
}

#[test]
fn readfile_uses_virtual_text_overlays_and_byte_limits() {
    let request = json!({"version":1,"op":"assemble","format":"kobj","roots":["/virtual/main.asm"],"sources":[
        {"path":"/virtual/main.asm","text":"SECTION \"code\",ROM0[0]\ndb READFILE(\"note.txt\",5)\ndb STRUPR(READFILE(\"note.txt\",2))\ndb BYTELEN(READFILE(\"note.txt\"))\n"},
        {"path":"/virtual/note.txt","text":"hello é"}
    ]});
    assert_eq!(
        section_bytes(&process(&request.to_string())),
        b"helloHE\x08"
    );
}

#[test]
fn readfile_rejects_an_overlay_limit_that_splits_utf8() {
    for (limit, valid) in [(1, false), (2, true)] {
        let request = json!({"version":1,"op":"assemble","format":"kobj","roots":["/virtual/main.asm"],"sources":[
            {"path":"/virtual/main.asm","text":format!("SECTION \"code\",ROM0[0]\ndb BYTELEN(READFILE(\"note.txt\",{limit}))\n")},
            {"path":"/virtual/note.txt","text":"é"}
        ]});
        let response = process(&request.to_string());
        assert_eq!(response["ok"], valid, "{response}");
        if valid {
            assert_eq!(section_bytes(&response), [2]);
        } else {
            assert!(
                response["diagnostics"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .any(|d| d["message"].as_str().unwrap().contains("UTF-8"))
            );
        }
    }
}

#[test]
fn physical_includes_and_readfile_resolve_percent_encoded_file_uris() {
    struct Fixture(std::path::PathBuf);
    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let fixture = Fixture(
        std::env::temp_dir().join(format!("koh-service-uri-{} space%20 é", std::process::id())),
    );
    std::fs::create_dir(&fixture.0).unwrap();
    std::fs::write(fixture.0.join("header.inc"), "db 1\n").unwrap();
    std::fs::write(fixture.0.join("note.txt"), "ok").unwrap();
    let path = format!(
        "file://{}",
        fixture
            .0
            .join("main.asm")
            .to_string_lossy()
            .replace('%', "%25")
            .replace(' ', "%20")
            .replace('é', "%C3%A9")
    );
    let request = json!({"version":1,"op":"assemble","format":"kobj","roots":[path.clone()],"sources":[
        {"path":path,"text":"SECTION \"code\",ROM0[0]\nINCLUDE \"header.inc\"\ndb READFILE(\"note.txt\")\n"}
    ]});
    assert_eq!(
        section_bytes(&process(&request.to_string())),
        [1, b'o', b'k']
    );
}

#[test]
fn dynamic_include_and_readfile_overlay_paths_ignore_case() {
    let request = json!({"version":1,"op":"assemble","format":"kobj","roots":["/virtual/main.asm"],"sources":[
        {"path":"/virtual/main.asm","text":"DEF File EQUS \"\\\"defs.inc\\\"\"\nSECTION \"code\",ROM0[0]\nINCLUDE File\ndb READFILE(\"note.txt\")\nINCBIN \"asset.bin\"\n"},
        {"path":"/virtual/Defs.inc","text":"Child: db 7\n"},
        {"path":"/virtual/Note.TXT","text":"ok"}
    ],"binaryFiles":[{"path":"/virtual/Asset.BIN","data":[9]}]});
    let response = process(&request.to_string());
    assert_eq!(section_bytes(&response), [7, b'o', b'k', 9]);
    assert!(
        response["symbols"]
            .as_array()
            .unwrap()
            .iter()
            .any(|s| s["name"] == "Child")
    );
}
