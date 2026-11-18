use std::path::Path;

pub fn images<const N: usize>(fixture: &str, build: impl FnOnce() -> [Vec<u8>; N]) -> [Vec<u8>; N] {
    let run = std::env::var("NEXTEST_RUN_ID")
        .ok()
        .filter(|_| std::env::var("NEXTEST_TEST_GROUP").as_deref() == Ok("roslyn"));
    let directory = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests/fixtures")
        .join(fixture)
        .join("obj/koh-nextest-images");
    cached(&directory, run.as_deref(), || {
        eprintln!("Build metadata fixture {fixture}");
        build()
    })
}

pub(super) fn cached<const N: usize>(
    directory: &Path,
    run: Option<&str>,
    build: impl FnOnce() -> [Vec<u8>; N],
) -> [Vec<u8>; N] {
    let Some(run) = run else {
        return build();
    };
    let marker = directory.join("run");
    if std::fs::read_to_string(&marker).ok().as_deref() == Some(run) {
        let images: [_; N] =
            std::array::from_fn(|index| std::fs::read(directory.join(format!("{index}.dll"))));
        if images.iter().all(Result::is_ok) {
            return images.map(Result::unwrap);
        }
    }
    if marker.exists() {
        std::fs::remove_file(&marker).unwrap();
    }
    let images = build();
    std::fs::create_dir_all(directory).unwrap();
    for (index, image) in images.iter().enumerate() {
        std::fs::write(directory.join(format!("{index}.dll")), image).unwrap();
    }
    std::fs::write(marker, run).unwrap();
    images
}
