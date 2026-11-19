use koh_lsp::graph::Graph;
use koh_lsp::paths::key;
use std::collections::BTreeSet;

fn set(paths: &[&str]) -> BTreeSet<String> {
    paths.iter().map(|s| s.to_string()).collect()
}

#[test]
fn edges_replace_remove_and_track_scanned_leaves_case_insensitively() {
    let mut graph = Graph::default();
    assert!(graph.includes("unknown").is_empty());
    assert!(graph.includers("unknown").is_empty());
    assert!(!graph.scanned("leaf.inc"));
    graph.upsert("Main.asm", &["old.inc".into(), "shared.inc".into()]);
    assert_eq!(graph.includes("MAIN.ASM"), set(&["old.inc", "shared.inc"]));
    assert_eq!(graph.includers("OLD.INC"), set(&["Main.asm"]));
    graph.upsert("Main.asm", &["new.inc".into()]);
    assert!(graph.includers("old.inc").is_empty());
    assert_eq!(graph.includes("Main.asm"), set(&["new.inc"]));
    graph.upsert("leaf.inc", &[]);
    assert!(graph.scanned("LEAF.INC"));
    graph.remove("leaf.inc");
    assert!(!graph.scanned("leaf.inc"));
    graph.remove("NEW.INC");
    assert!(graph.includes("Main.asm").is_empty());
    graph.upsert("Main.asm", &["new.inc".into()]);
    graph.remove("Main.asm");
    assert!(graph.includers("new.inc").is_empty());
    graph.remove("missing");
    graph.upsert("a.asm", &["shared.inc".into()]);
    graph.upsert("b.asm", &["shared.inc".into()]);
    assert_eq!(graph.includers("shared.inc"), set(&["a.asm", "b.asm"]));
}

#[test]
fn reachable_files_and_entrypoints_handle_chains_diamonds_and_cycles() {
    let mut graph = Graph::default();
    assert_eq!(graph.reachable("alone.asm"), set(&["alone.asm"]));
    graph.upsert("a.asm", &["b.asm".into(), "c.asm".into()]);
    graph.upsert("b.asm", &["d.inc".into()]);
    graph.upsert("c.asm", &["d.inc".into()]);
    graph.upsert("d.inc", &["a.asm".into()]);
    assert_eq!(
        graph.reachable("a.asm"),
        set(&["a.asm", "b.asm", "c.asm", "d.inc"])
    );
    assert_eq!(
        graph.reachable_entrypoints("d.inc", &set(&["a.asm", "b.asm", "unrelated.asm"])),
        set(&["a.asm", "b.asm"])
    );
    assert!(
        graph
            .reachable_entrypoints("isolated", &set(&["a.asm"]))
            .is_empty()
    );
}

#[test]
fn discovery_scores_candidates_and_uses_distance_then_alphabetical_ownership() {
    let mut graph = Graph::default();
    graph.upsert("a.asm", &["mid.inc".into()]);
    graph.upsert("mid.inc", &["shared.inc".into()]);
    graph.upsert("z.asm", &["shared.inc".into()]);
    graph.upsert("plain.asm", &[]);
    graph.upsert("open.asm", &[]);
    graph.upsert("open.inc", &[]);
    graph.upsert("closed.inc", &[]);
    let files = set(&[
        "a.asm",
        "mid.inc",
        "shared.inc",
        "z.asm",
        "plain.asm",
        "open.asm",
        "open.inc",
        "closed.inc",
    ]);
    let open = set(&["open.asm", "open.inc"]);
    let result = graph.discover(&files, &open);
    assert_eq!(result.owners[&key("a.asm")], "a.asm");
    assert!(!result.owners.contains_key(&key("closed.inc")));
    assert_eq!(
        result
            .candidates
            .iter()
            .map(|c| (c.path.as_str(), c.score))
            .collect::<Vec<_>>(),
        vec![
            ("a.asm", 4),
            ("z.asm", 4),
            ("open.asm", 3),
            ("plain.asm", 2),
            ("open.inc", 1)
        ]
    );
    assert_eq!(result.owners[&key("shared.inc")], "z.asm");
    graph.upsert("a.asm", &["shared.inc".into()]);
    assert_eq!(
        graph.discover(&files, &open).owners[&key("shared.inc")],
        "a.asm"
    );
    graph.upsert("loop.asm", &["loop2.asm".into()]);
    graph.upsert("loop2.asm", &["loop.asm".into()]);
    assert!(
        graph
            .discover(&set(&["loop.asm", "loop2.asm"]), &set(&[]))
            .candidates
            .is_empty()
    );
    let cycles = graph.discover(&set(&["loop.asm", "loop2.asm"]), &set(&["loop.asm"]));
    assert!(cycles.candidates.is_empty());
    let included_open = graph.discover(&set(&["a.asm", "shared.inc"]), &set(&["shared.inc"]));
    assert_eq!(included_open.candidates.len(), 1);
    assert_eq!(included_open.candidates[0].path, "a.asm");
}
