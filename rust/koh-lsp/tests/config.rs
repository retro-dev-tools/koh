use koh_lsp::config::{Configuration, Project};
use std::path::Path;

#[test]
fn valid_projects_normalize_paths_and_ignore_unrelated_fields() {
    let root = std::env::temp_dir();
    assert_eq!(
        Configuration::parse(
            "version: 1\noutput: game.gb\nprojects:\n  - name: game\n    entrypoint: src/../game.asm\n  - name: tools\n    entrypoint: tools.asm",
            &root
        ),
        Configuration::Configured(vec![
            Project {
                name: "game".into(),
                entrypoint: root.join("game.asm")
            },
            Project {
                name: "tools".into(),
                entrypoint: root.join("tools.asm")
            },
        ])
    );
}

#[test]
fn valid_single_project_retains_its_name_and_absolute_entrypoint() {
    let root = std::env::temp_dir();
    assert_eq!(
        Configuration::parse(
            "version: 1\nprojects:\n  - name: game\n    entrypoint: src/game.asm\n",
            &root
        ),
        Configuration::Configured(vec![Project {
            name: "game".into(),
            entrypoint: root.join("src/game.asm")
        }])
    )
}

#[test]
fn invalid_configuration_does_not_become_heuristic() {
    for (yaml, message) in [
        ("", "empty"),
        ("version: 1\nprojects: [[[invalid", "YAML"),
        ("projects: []", "version"),
        ("version: 2\nprojects: []", "version"),
        ("version: 1", "projects"),
        ("version: 1\nprojects: []", "empty"),
        ("version: 1\nprojects: [{entrypoint: game.asm}]", "name"),
        ("version: 1\nprojects: [{name: game}]", "entrypoint"),
        (
            "version: 1\nprojects: [{name: ' ', entrypoint: game.asm}]",
            "name",
        ),
        (
            "version: 1\nprojects: [{name: game, entrypoint: ' '}]",
            "entrypoint",
        ),
        (
            "version: 1\nprojects: [{name: game, entrypoint: a.asm}, {name: GAME, entrypoint: b.asm}]",
            "Duplicate project name",
        ),
        (
            "version: 1\nprojects: [{name: game, entrypoint: a.asm}, {name: tools, entrypoint: src/../a.asm}]",
            "Duplicate entrypoint",
        ),
        (
            "version: 1\nprojects: [{name: game, entrypoint: /absolute.asm}]",
            "relative",
        ),
    ] {
        let Configuration::Invalid(errors) = Configuration::parse(yaml, Path::new("/workspace"))
        else {
            panic!("accepted {yaml}");
        };
        assert!(
            errors.iter().any(|e| e.contains(message)),
            "{yaml}: {errors:?}"
        );
    }
}

#[test]
fn absent_config_selects_heuristics() {
    assert_eq!(
        Configuration::load(
            &std::env::temp_dir().join(format!("koh-no-config-{}", std::process::id()))
        ),
        Configuration::Heuristic
    );
}
