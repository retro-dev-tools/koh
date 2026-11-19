mod common;
use common::Fixture;
use koh_lsp::workspace::Workspace;
use serde_json::Value;

fn symbols(analysis: &Value) -> Vec<&str> {
    analysis["symbols"]
        .as_array()
        .unwrap()
        .iter()
        .filter_map(|s| s["name"].as_str())
        .collect()
}

#[test]
fn configured_projects_isolate_symbols_diagnostics_and_unrelated_files() {
    let f = Fixture::new();
    f.write("koh.yaml","version: 1\nprojects: [{name: game, entrypoint: game.asm}, {name: tools, entrypoint: tools.asm}]");
    let game = f.write(
        "game.asm",
        "SECTION \"Game\",ROM0\nGameStart: nop\nld a,Missing\n",
    );
    let tools = f.write("tools.asm", "SECTION \"Tools\",ROM0\nToolsMain: nop\n");
    let other = f.0.join("unrelated.asm");
    let mut w = Workspace::default();
    w.initialize(&f.0).unwrap();
    w.open(&game, &std::fs::read_to_string(&game).unwrap())
        .unwrap();
    w.open(&tools, &std::fs::read_to_string(&tools).unwrap())
        .unwrap();
    w.open(&other, "SECTION \"Other\",ROM0\nnop").unwrap();
    let g = w.project_for(&game).unwrap();
    assert!(symbols(&g.analysis).contains(&"GameStart"));
    assert!(!symbols(&g.analysis).contains(&"ToolsMain"));
    assert!(!w.diagnostics(&game).is_empty());
    assert!(w.diagnostics(&tools).is_empty());
    assert!(w.project_for(&other).is_none());
    assert_eq!(w.projects().len(), 2);
    assert_eq!(w.projects()[0].entrypoint, game.to_string_lossy());
    assert_eq!(w.projects()[1].entrypoint, tools.to_string_lossy());
    assert_eq!(w.projects()[0].name, "game");
    assert_eq!(w.projects()[1].name, "tools");
}

#[test]
fn included_only_open_file_uses_root_context_and_unsaved_overlay() {
    let f = Fixture::new();
    f.write(
        "koh.yaml",
        "version: 1\nprojects: [{name: game, entrypoint: src/game.asm}]",
    );
    let game = f.write(
        "src/game.asm",
        "INCLUDE \"macros.inc\"\nSECTION \"Game\",ROM0\nINCLUDE \"home/entry.asm\"\n",
    );
    f.write("src/macros.inc", "MACRO load_value\n ld a,\\1\nENDM\n");
    let leaf = f.write("src/home/entry.asm", "Entry: load_value 1\n");
    let mut w = Workspace::default();
    w.initialize(&f.0).unwrap();
    w.open(&leaf, "UnsavedEntry: load_value 2\n").unwrap();
    let project = w.project_for(&leaf).unwrap();
    assert_eq!(project.entrypoint, game.to_string_lossy());
    assert_eq!(project.name, "game");
    assert_eq!(w.projects().len(), 1);
    assert!(symbols(&project.analysis).contains(&"UnsavedEntry"));
    assert!(!symbols(&project.analysis).contains(&"Entry"));
    assert!(
        w.diagnostics(&leaf).is_empty(),
        "{:?}",
        w.diagnostics(&leaf)
    );
    w.close(&leaf).unwrap();
    assert!(w.document(&leaf).is_none());
    assert!(symbols(&w.project_for(&leaf).unwrap().analysis).contains(&"Entry"));
}

#[test]
fn shared_include_has_separate_contexts_and_deterministic_primary_owner() {
    let f = Fixture::new();
    f.write(
        "koh.yaml",
        "version: 1\nprojects: [{name: z, entrypoint: z.asm}, {name: a, entrypoint: a.asm}]",
    );
    let a = f.write(
        "a.asm",
        "SECTION \"A\",ROM0\nDEF Value EQU 1\nINCLUDE \"shared.inc\"\n",
    );
    f.write(
        "z.asm",
        "SECTION \"Z\",ROM0\nDEF Value EQU 2\nINCLUDE \"shared.inc\"\n",
    );
    let leaf = f.write("shared.inc", "Shared: ld a,Value\n");
    let mut w = Workspace::default();
    w.initialize(&f.0).unwrap();
    w.open(&leaf, "Shared: ld a,Value\n").unwrap();
    assert_eq!(
        w.project_for(&leaf).unwrap().entrypoint,
        a.to_string_lossy()
    );
    assert_eq!(w.projects().iter().filter(|p| p.contains(&leaf)).count(), 2);
    let values: Vec<_> = w
        .projects()
        .iter()
        .map(|p| {
            p.analysis["symbols"]
                .as_array()
                .unwrap()
                .iter()
                .find(|s| s["name"] == "Value")
                .unwrap()["value"]
                .as_i64()
                .unwrap()
        })
        .collect();
    assert_eq!(values, vec![1, 2]);
}

#[test]
fn heuristic_roots_react_to_unsaved_include_edge_changes() {
    let f = Fixture::new();
    let root = f.0.join("main.asm");
    let leaf = f.0.join("body.inc");
    let mut w = Workspace::default();
    w.initialize(&f.0).unwrap();
    w.open(&leaf, "Target: nop\n").unwrap();
    assert_eq!(w.projects().len(), 1);
    w.open(
        &root,
        "SECTION \"Main\",ROM0\nINCLUDE \"body.inc\"\njp Target\n",
    )
    .unwrap();
    assert_eq!(w.projects().len(), 1);
    assert_eq!(
        w.project_for(&leaf).unwrap().entrypoint,
        root.to_string_lossy()
    );
    w.open(&root, "SECTION \"Main\",ROM0\nnop\n").unwrap();
    assert_eq!(w.projects().len(), 2);
    assert_eq!(
        w.project_for(&leaf).unwrap().entrypoint,
        leaf.to_string_lossy()
    );
}

#[test]
fn invalid_config_is_parse_only_until_reload_and_open_state_is_preserved() {
    let f = Fixture::new();
    f.write("koh.yaml", "version: 99");
    let path = f.0.join("main.asm");
    let mut w = Workspace::default();
    w.initialize(&f.0).unwrap();
    assert!(
        matches!(&w.configuration, Some(koh_lsp::config::Configuration::Invalid(errors)) if errors.len() == 2)
    );
    w.open(&path, "SECTION \"Main\",ROM0\nLabel: nop\n")
        .unwrap();
    assert!(w.projects().is_empty());
    assert!(w.project_for(&path).is_none());
    w.open(&path, "ld a,\n").unwrap();
    assert!(!w.diagnostics(&path).is_empty());
    f.write(
        "koh.yaml",
        "version: 1\nprojects: [{name: game, entrypoint: main.asm}]",
    );
    w.reload().unwrap();
    assert_eq!(w.projects().len(), 1);
    assert_eq!(w.document(&path).unwrap().text, "ld a,\n");
}

#[test]
fn standalone_open_change_close_and_case_insensitive_lookup() {
    let f = Fixture::new();
    let path = f.0.join("main.asm");
    let mut w = Workspace::default();
    assert!(w.document(&path).is_none());
    assert!(w.diagnostics(&path).is_empty());
    w.open(&path, "SECTION \"Main\",ROM0\nOld: nop").unwrap();
    assert!(
        w.document(std::path::Path::new(&path.to_string_lossy().to_uppercase()))
            .is_some()
    );
    assert_eq!(w.open_documents().count(), 1);
    w.open(&path, "SECTION \"Main\",ROM0\nNew: nop").unwrap();
    assert!(symbols(&w.project_for(&path).unwrap().analysis).contains(&"New"));
    assert!(!symbols(&w.project_for(&path).unwrap().analysis).contains(&"Old"));
    w.open(&path, "ld a,\n").unwrap();
    assert!(!w.diagnostics(&path).is_empty());
    w.close(&path).unwrap();
    assert_eq!(w.open_documents().count(), 0);
    assert!(w.projects().is_empty());
}

#[test]
fn unsaved_text_does_not_replace_physical_incbin_bytes() {
    let f = Fixture::new();
    f.write(
        "koh.yaml",
        "version: 1\nprojects: [{name: game, entrypoint: main.asm}]",
    );
    let asset = f.0.join("asset.bin");
    std::fs::write(&asset, [8, 9]).unwrap();
    let root = f.write(
        "main.asm",
        "SECTION \"Main\",ROM0\nINCBIN \"asset.bin\"\nAfter: nop\n",
    );
    let mut w = Workspace::default();
    w.initialize(&f.0).unwrap();
    w.open(&asset, "unsaved text").unwrap();
    w.open(&root, &std::fs::read_to_string(&root).unwrap())
        .unwrap();
    let analysis = &w.project_for(&root).unwrap().analysis;
    let after = analysis["symbols"]
        .as_array()
        .unwrap()
        .iter()
        .find(|s| s["name"] == "After")
        .unwrap();
    assert_eq!(after["value"], 2);
}

#[test]
fn shared_nested_include_uses_each_entrypoints_root_before_its_own_directory() {
    let f = Fixture::new();
    f.write("koh.yaml", "version: 1\nprojects: [{name: a, entrypoint: a/main.asm}, {name: b, entrypoint: b/main.asm}]");
    let a = f.write(
        "a/main.asm",
        "SECTION \"A\",ROM0\nINCLUDE \"../shared.inc\"\n",
    );
    let b = f.write(
        "b/main.asm",
        "SECTION \"B\",ROM0\nINCLUDE \"../shared.inc\"\n",
    );
    f.write("shared.inc", "INCLUDE \"leaf.inc\"\nShared: db Value\n");
    f.write("leaf.inc", "DEF Value EQU 99\n");
    let a_leaf = f.write("a/leaf.inc", "DEF Value EQU 1\n");
    let b_leaf = f.write("b/leaf.inc", "DEF Value EQU 2\n");
    let mut w = Workspace::default();
    w.initialize(&f.0).unwrap();
    for (root, expected, own, other) in [(&a, 1, &a_leaf, &b_leaf), (&b, 2, &b_leaf, &a_leaf)] {
        let project = w.project_for(root).unwrap();
        let symbol = project.analysis["symbols"]
            .as_array()
            .unwrap()
            .iter()
            .find(|s| s["name"] == "Value")
            .unwrap();
        assert_eq!(symbol["value"], expected);
        assert!(project.contains(own));
        assert!(!project.contains(other));
    }
}

#[test]
fn editing_one_project_preserves_unrelated_project_analysis() {
    let f = Fixture::new();
    f.write(
        "koh.yaml",
        "version: 1\nprojects: [{name: a, entrypoint: a.asm}, {name: b, entrypoint: b.asm}]",
    );
    let a = f.write("a.asm", "SECTION \"A\",ROM0\nnop\n");
    let b = f.write("b.asm", "SECTION \"B\",ROM0\nDEF Value EQU 2\n");
    let mut w = Workspace::default();
    w.initialize(&f.0).unwrap();
    let before = w.project_for(&b).unwrap().analysis.clone();
    f.write("b.asm", "SECTION \"B\",ROM0\nDEF Value EQU 99\n");
    w.open(&a, "SECTION \"A\",ROM0\nhalt\n").unwrap();
    assert_eq!(w.project_for(&b).unwrap().analysis, before);
    w.open(&b, &std::fs::read_to_string(&b).unwrap()).unwrap();
    assert_ne!(w.project_for(&b).unwrap().analysis, before);
}

#[test]
fn shared_include_edit_rebuilds_all_owning_projects() {
    let f = Fixture::new();
    f.write(
        "koh.yaml",
        "version: 1\nprojects: [{name: a, entrypoint: a.asm}, {name: b, entrypoint: b.asm}]",
    );
    f.write("a.asm", "SECTION \"A\",ROM0\nINCLUDE \"shared.inc\"\n");
    f.write("b.asm", "SECTION \"B\",ROM0\nINCLUDE \"shared.inc\"\n");
    let shared = f.write("shared.inc", "DEF Value EQU 1\n");
    let mut w = Workspace::default();
    w.initialize(&f.0).unwrap();
    for expected in [1, 2] {
        if expected == 2 {
            w.open(&shared, "DEF Value EQU 2\n").unwrap();
        }
        assert_eq!(w.projects().len(), 2);
        for project in w.projects() {
            let value = project.analysis["symbols"]
                .as_array()
                .unwrap()
                .iter()
                .find(|s| s["name"] == "Value")
                .unwrap();
            assert_eq!(value["value"], expected);
        }
    }
}

#[test]
fn heuristic_shared_file_owner_is_alphabetically_deterministic() {
    let f = Fixture::new();
    let alpha = f.0.join("alpha.asm");
    let zeta = f.0.join("zeta.asm");
    let shared = f.write("shared.inc", "Shared: nop\n");
    let mut w = Workspace::default();
    w.initialize(&f.0).unwrap();
    w.open(&zeta, "SECTION \"Z\",ROM0\nINCLUDE \"shared.inc\"\n")
        .unwrap();
    w.open(&alpha, "SECTION \"A\",ROM0\nINCLUDE \"shared.inc\"\n")
        .unwrap();
    assert_eq!(
        w.project_for(&shared).unwrap().entrypoint,
        alpha.to_string_lossy()
    );
    w.close(&alpha).unwrap();
    assert_eq!(
        w.project_for(&shared).unwrap().entrypoint,
        zeta.to_string_lossy()
    );
    w.close(&zeta).unwrap();
    assert!(w.projects().is_empty());
}

#[test]
fn heuristic_orphan_sources_keep_independent_analysis() {
    let f = Fixture::new();
    let asm = f.0.join("orphan.asm");
    let inc = f.0.join("orphan.inc");
    let mut w = Workspace::default();
    w.initialize(&f.0).unwrap();
    w.open(&asm, "SECTION \"Main\",ROM0\nOrphan: nop\n")
        .unwrap();
    w.open(&inc, "DEF Value EQU 42\n").unwrap();
    assert_eq!(w.projects().len(), 2);
    assert!(symbols(&w.project_for(&asm).unwrap().analysis).contains(&"Orphan"));
    assert!(!symbols(&w.project_for(&asm).unwrap().analysis).contains(&"Value"));
    assert!(symbols(&w.project_for(&inc).unwrap().analysis).contains(&"Value"));
}

#[test]
fn overlay_text_updates_case_insensitively_and_close_restores_disk_reads() {
    let f = Fixture::new();
    let path = f.write("source.asm", "disk text");
    let mut w = Workspace::default();
    assert_eq!(w.text(&path).unwrap(), "disk text");
    w.open(&path, "unsaved one").unwrap();
    assert_eq!(w.text(&path).unwrap(), "unsaved one");
    w.open(&path, "unsaved two").unwrap();
    let upper = std::path::PathBuf::from(path.to_string_lossy().to_uppercase());
    assert_eq!(w.text(&upper).unwrap(), "unsaved two");
    w.close(&upper).unwrap();
    assert_eq!(w.text(&path).unwrap(), "disk text");
    assert!(w.text(&f.0.join("missing.asm")).is_err());
}

#[test]
fn missing_relative_include_is_resolved_from_its_containing_file() {
    let f = Fixture::new();
    let path = f.0.join("src/main.asm");
    let mut w = Workspace::default();
    w.initialize(&f.0).unwrap();
    w.open(&path, "INCLUDE \"helpers/math.asm\"\n").unwrap();
    assert!(
        w.project_for(&path)
            .unwrap()
            .contains(&f.0.join("src/helpers/math.asm"))
    );
    assert!(!w.diagnostics(&path).is_empty());
}
