mod common;
use common::Fixture;
use koh_lsp::worker::Worker;
use serde_json::json;

#[test]
fn readfile_uses_entrypoint_root_and_byte_limit_for_disk_and_overlays() {
    let mut worker = Worker::assembler().unwrap();
    for virtual_files in [false, true] {
        let f = Fixture::new();
        let root = f.0.join("src");
        let main = root.join("main.asm");
        let body = root.join("sub/body.inc");
        let files = [
            (
                main.clone(),
                "SECTION \"Main\",ROM0\nINCLUDE \"sub/body.inc\"",
            ),
            (body.clone(), "db READFILE(\"note.txt\",2)"),
            (root.join("note.txt"), "ABtail"),
            (root.join("sub/note.txt"), "ZZ"),
        ];
        if !virtual_files {
            for (path, text) in &files {
                f.write(path.strip_prefix(&f.0).unwrap().to_str().unwrap(), text);
            }
        }
        let sources = if virtual_files {
            files
                .iter()
                .map(|(path, text)| json!({"path":path,"text":text}))
                .collect::<Vec<_>>()
        } else {
            vec![json!({"path":main,"text":files[0].1})]
        };
        let binary = if virtual_files {
            files
                .iter()
                .map(|(path, text)| json!({"path":path,"data":text.as_bytes()}))
                .collect::<Vec<_>>()
        } else {
            vec![]
        };
        let response = worker.request(&json!({"version":1,"op":"analyze","sources":sources,"roots":[main],"searchRoots":[{"file":main,"roots":[root]}],"binaryFiles":binary})).unwrap();
        assert_eq!(response["ok"], true, "{response}");
        assert_eq!(response["object"]["sections"][0]["data"], json!([65, 66]));
    }
}

#[test]
fn explicit_resolution_mappings_control_nested_physical_and_virtual_includes() {
    let mut worker = Worker::assembler().unwrap();
    for virtual_files in [false, true] {
        let f = Fixture::new();
        let root = f.0.join("src");
        let main = root.join("main.asm");
        let body = root.join("sub/body.inc");
        let constants = root.join("constants.inc");
        let files = [
            (
                main.clone(),
                "SECTION \"Main\",ROM0\nINCLUDE \"sub/body.inc\"",
            ),
            (
                body.clone(),
                "INCLUDE \"constants.inc\"\nINCBIN \"asset.bin\"\nld a,VALUE",
            ),
            (constants.clone(), "DEF VALUE EQU 42"),
            (root.join("sub/constants.inc"), "DEF VALUE EQU 99"),
        ];
        if !virtual_files {
            for (path, text) in &files {
                f.write(path.strip_prefix(&f.0).unwrap().to_str().unwrap(), text);
            }
            std::fs::write(root.join("asset.bin"), [7, 8]).unwrap();
            std::fs::write(root.join("sub/asset.bin"), [9, 9]).unwrap();
        }
        let sources = if virtual_files {
            files
                .iter()
                .map(|(path, text)| json!({"path":path,"text":text}))
                .collect::<Vec<_>>()
        } else {
            vec![json!({"path":main,"text":files[0].1})]
        };
        let binary = if virtual_files {
            vec![
                json!({"path":root.join("asset.bin"),"data":[7,8]}),
                json!({"path":root.join("sub/asset.bin"),"data":[9,9]}),
            ]
        } else {
            vec![]
        };
        let response=worker.request(&json!({"version":1,"op":"analyze","sources":sources,"roots":[main],"binaryFiles":binary,"resolutions":[
            {"includingFile":main,"requested":"sub/body.inc","resolved":body},
            {"includingFile":body,"requested":"constants.inc","resolved":constants},
            {"includingFile":body,"requested":"asset.bin","resolved":root.join("asset.bin")}
        ]})).unwrap();
        assert_eq!(response["ok"], true, "{response}");
        assert_eq!(
            response["object"]["sections"][0]["data"],
            json!([7, 8, 0x3e, 42])
        );
        let value = response["symbols"]
            .as_array()
            .unwrap()
            .iter()
            .find(|s| s["name"] == "VALUE")
            .unwrap();
        assert_eq!(
            value["definition"]["path"],
            constants.to_string_lossy().as_ref()
        );
    }
}
