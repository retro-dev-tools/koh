#[path = "support/image_cache.rs"]
mod image_cache;
use image_cache::cached;
use std::cell::Cell;

#[test]
fn fixture_images_are_private_and_rebuilt_for_each_run() {
    let directory = tempfile::tempdir().unwrap();
    let builds = Cell::new(0);
    let build = || {
        builds.set(builds.get() + 1);
        [vec![1, 2], vec![3, 4]]
    };
    let mut first = cached(directory.path(), Some("first"), build);
    first[0][0] = 99;
    let second = cached(directory.path(), Some("first"), || {
        panic!("Successful fixture must be reused")
    });
    assert_eq!(second, [vec![1, 2], vec![3, 4]]);
    assert_eq!(builds.get(), 1);
    assert_eq!(cached(directory.path(), Some("next"), build), second);
    assert_eq!(builds.get(), 2);
}

#[test]
fn failed_fixture_build_cannot_publish_a_reuse_marker() {
    let directory = tempfile::tempdir().unwrap();
    cached(directory.path(), Some("old"), || [vec![1]]);
    assert!(
        std::panic::catch_unwind(|| {
            cached::<1>(directory.path(), Some("new"), || {
                panic!("Fixture build failed")
            })
        })
        .is_err()
    );
    assert!(!directory.path().join("run").exists());
    assert_eq!(
        cached(directory.path(), Some("new"), || [vec![2]]),
        [vec![2]]
    );
}

#[test]
fn incomplete_fixture_images_require_a_fresh_build() {
    let directory = tempfile::tempdir().unwrap();
    cached(directory.path(), Some("run"), || [vec![1], vec![2]]);
    std::fs::remove_file(directory.path().join("1.dll")).unwrap();
    assert_eq!(
        cached(directory.path(), Some("run"), || [vec![3], vec![4]]),
        [vec![3], vec![4]]
    );
}

#[test]
fn outside_nextest_images_are_always_built_without_disk_reuse() {
    let directory = tempfile::tempdir().unwrap();
    let builds = Cell::new(0);
    for expected in [1, 2] {
        assert_eq!(
            cached(directory.path(), None, || {
                builds.set(builds.get() + 1);
                [vec![builds.get()]]
            }),
            [vec![expected]]
        );
    }
    assert_eq!(std::fs::read_dir(directory.path()).unwrap().count(), 0);
    assert_eq!(
        image_cache::images(directory.path().to_str().unwrap(), || [vec![9]]),
        [vec![9]]
    );
}
