mod common;
use common::Fixture;
use koh_lsp::{
    symbols::{occurrences, resolve_at, validate_rename},
    workspace::Workspace,
};

fn at(text: &str, marker: &str) -> usize {
    text[..text.find(marker).unwrap()].encode_utf16().count()
}

#[test]
fn resolve_and_occurrences_preserve_types_contexts_and_local_scope() {
    let f = Fixture::new();
    let path = f.0.join("main.asm");
    let text = "SECTION \"Main\",ROM0\nDEF Value EQU 42\nMACRO load_value\n ld a,\\1\nENDM\nGlobal1:\n.local: nop\n jr .local\nGlobal2:\n.local: load_value Value\n jr .local\n db \"Value\"\n";
    let mut w = Workspace::default();
    w.open(&path, text).unwrap();
    for (marker, name, declaration, count) in [
        ("Value EQU", "Value", true, 2),
        ("load_value\n", "load_value", true, 2),
        ("Global1:", "Global1", true, 1),
        (".local: nop", "Global1.local", true, 2),
        (".local: load", "Global2.local", true, 2),
    ] {
        let target = resolve_at(&w, &path, at(text, marker)).unwrap();
        assert_eq!(target.symbol["name"], name);
        assert_eq!(target.declaration, declaration);
        assert_eq!(occurrences(&w, &target, true).len(), count);
    }
    let last = text.rfind(".local").unwrap();
    let target = resolve_at(&w, &path, text[..last].encode_utf16().count()).unwrap();
    assert_eq!(target.symbol["name"], "Global2.local");
    assert!(!target.declaration);
    assert_eq!(occurrences(&w, &target, false).len(), 1);
    for marker in ["SECTION", "ROM0", "nop", "a,", "\\1", "\"Value\""] {
        assert!(
            resolve_at(&w, &path, at(text, marker)).is_none(),
            "{marker}"
        );
    }
}

#[test]
fn include_owned_symbol_finds_root_references_and_included_declaration() {
    let f = Fixture::new();
    f.write(
        "koh.yaml",
        "version: 1\nprojects: [{name: game, entrypoint: main.asm}]",
    );
    let source = "SECTION \"Main\",ROM0\nINCLUDE \"defs.inc\"\njp Target\ncall Target\n";
    let root = f.write("main.asm", source);
    let leaf = f.write("defs.inc", "Target: nop\n");
    let mut w = Workspace::default();
    w.initialize(&f.0).unwrap();
    w.open(&root, source).unwrap();
    w.open(&leaf, "Target: nop\n").unwrap();
    let target = resolve_at(&w, &root, at(source, "Target")).unwrap();
    assert!(!target.declaration);
    let found = occurrences(&w, &target, true);
    assert_eq!(found.len(), 3);
    assert_eq!(found.iter().filter(|item| item.declaration).count(), 1);
    assert_eq!(occurrences(&w, &target, false).len(), 2);
}

#[test]
fn rename_targets_include_numeric_string_constants_macros_and_reference_origins() {
    let fixture = Fixture::new();
    let path = fixture.write("types.asm", "");
    for (source, marker, name, count, declaration) in [
        (
            "SECTION \"Main\",ROM0\nMyLabel: nop\njp MyLabel\ncall MyLabel\n",
            "MyLabel:",
            "MyLabel",
            3,
            true,
        ),
        (
            "SECTION \"Main\",ROM0\nMyLabel: nop\njp MyLabel\ncall MyLabel\n",
            "MyLabel\n",
            "MyLabel",
            3,
            false,
        ),
        (
            "DEF MyConst EQU 42\nSECTION \"Main\",ROM0\nld a,MyConst\n",
            "MyConst EQU",
            "MyConst",
            2,
            true,
        ),
        (
            "DEF Greeting EQUS \"\\\"hello\\\"\"\nSECTION \"Main\",ROM0\ndb Greeting\n",
            "Greeting EQUS",
            "Greeting",
            2,
            true,
        ),
        (
            "MACRO MyMacro\nnop\nENDM\nSECTION \"Main\",ROM0\nMyMacro\nMyMacro\n",
            "MyMacro\n",
            "MyMacro",
            3,
            true,
        ),
    ] {
        let mut workspace = Workspace::default();
        workspace.open(&path, source).unwrap();
        let target = resolve_at(&workspace, &path, at(source, marker)).unwrap();
        assert_eq!(target.symbol["name"], name);
        assert_eq!(target.declaration, declaration);
        let found = occurrences(&workspace, &target, true);
        assert_eq!(found.len(), count, "{source}");
        assert_eq!(found.iter().filter(|item| item.declaration).count(), 1);
        let reference = found.iter().find(|item| !item.declaration).unwrap();
        let resolved = resolve_at(
            &workspace,
            &path,
            koh_lsp::symbols::span(&reference.token).0,
        )
        .unwrap();
        assert_eq!(resolved.symbol["id"], target.symbol["id"]);
        assert!(!resolved.declaration);
        assert!(
            validate_rename(&mut workspace, &target, "Renamed")
                .unwrap()
                .is_none()
        );
    }
}

#[test]
fn owner_local_and_exported_occurrences_do_not_conflate_shadowed_symbols() {
    let f = Fixture::new();
    let a = f.0.join("a.asm");
    let b = f.0.join("b.asm");
    let c = f.0.join("c.asm");
    let text = "SECTION \"A\",ROM0\nShared: nop\nEXPORT Shared\n";
    let mut w = Workspace::default();
    w.open(&a, text).unwrap();
    w.open(&b, "SECTION \"B\",ROM0\njp Shared\n").unwrap();
    w.open(&c, "SECTION \"C\",ROM0\nShared: jp Shared\n")
        .unwrap();
    let target = resolve_at(&w, &a, at(text, "Shared:")).unwrap();
    let found = occurrences(&w, &target, true);
    assert_eq!(found.len(), 3);
    assert!(found.iter().any(|o| o.path == a.to_string_lossy()));
    assert!(found.iter().any(|o| o.path == b.to_string_lossy()));
    assert!(!found.iter().any(|o| o.path == c.to_string_lossy()));
    assert_eq!(occurrences(&w, &target, false).len(), 2);
    let local = resolve_at(
        &w,
        &c,
        at("SECTION \"C\",ROM0\nShared: jp Shared\n", "Shared:"),
    )
    .unwrap();
    let found = occurrences(&w, &local, true);
    assert_eq!(found.len(), 2);
    assert!(found.iter().all(|o| o.path == c.to_string_lossy()));
    let local_source = "SECTION \"A\",ROM0\nShared: nop\n";
    w.open(&a, local_source).unwrap();
    let target = resolve_at(&w, &a, at(local_source, "Shared:")).unwrap();
    let found = occurrences(&w, &target, true);
    assert_eq!(found.len(), 1);
    assert_eq!(found[0].path, a.to_string_lossy());
}

#[test]
fn rename_rejects_invalid_forms_keywords_registers_and_collisions() {
    let f = Fixture::new();
    let path = f.0.join("main.asm");
    let text =
        "SECTION \"Main\",ROM0\nGlobal1:\n.loop: jr .loop\nGlobal2:\n.done: jr .done\nOther: nop\n";
    let mut w = Workspace::default();
    w.open(&path, text).unwrap();
    let global = resolve_at(&w, &path, at(text, "Global1:")).unwrap();
    for name in [
        "", " ", "123abc", "bad-name", "has.dot", "nop", "SECTION", "HL", "f", ".local", "Other",
    ] {
        assert!(
            validate_rename(&mut w, &global, name).unwrap().is_some(),
            "{name}"
        );
    }
    assert!(
        validate_rename(&mut w, &global, "Renamed")
            .unwrap()
            .is_none()
    );
    let local = resolve_at(&w, &path, at(text, ".loop:")).unwrap();
    assert!(
        validate_rename(&mut w, &local, "GlobalName")
            .unwrap()
            .is_some()
    );
    assert!(validate_rename(&mut w, &local, ".").unwrap().is_some());
    assert!(validate_rename(&mut w, &local, ".done").unwrap().is_none());
}

#[test]
fn rename_rejects_exported_collisions_and_allows_unrelated_owner_local_names() {
    let f = Fixture::new();
    let a = f.0.join("a.asm");
    let b = f.0.join("b.asm");
    let text = "SECTION \"A\",ROM0\nLabelA: nop\nEXPORT LabelA\n";
    let mut w = Workspace::default();
    w.open(&a, text).unwrap();
    w.open(&b, "SECTION \"B\",ROM0\nLabelB: nop\nEXPORT LabelB\n")
        .unwrap();
    let target = resolve_at(&w, &a, at(text, "LabelA:")).unwrap();
    assert!(
        validate_rename(&mut w, &target, "LabelB")
            .unwrap()
            .is_some()
    );
    w.open(&a, "SECTION \"A\",ROM0\nLocal: nop\n").unwrap();
    w.open(&b, "SECTION \"B\",ROM0\nOther: nop\n").unwrap();
    let target = resolve_at(&w, &a, at("SECTION \"A\",ROM0\nLocal: nop\n", "Local:")).unwrap();
    assert!(validate_rename(&mut w, &target, "Other").unwrap().is_none());
}
