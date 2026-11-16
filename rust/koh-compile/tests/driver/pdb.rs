use super::{Host, gameboy_images, host};
use koh_compile::{assembly::Assembly, pdb::PortablePdb};
use rstest::rstest;

#[rstest]
#[case("Debug")]
#[case("Release")]
fn portable_pdb_reads_real_roslyn_documents_and_hidden_ranges(
    host: &Host,
    #[case] configuration: &str,
) {
    let (directory, images) = gameboy_images(host, configuration, "SourceDebug");
    let assembly = Assembly::parse(&images[0]).unwrap();
    let bytes = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/SourceDebug.pdb")),
    )
    .unwrap();
    let pdb = PortablePdb::parse(&bytes, assembly.metadata().method_defs.len()).unwrap();
    let visible: Vec<_> = pdb
        .methods
        .iter()
        .flatten()
        .filter_map(|point| point.source.as_ref())
        .collect();
    for (name, line) in [
        ("mapped source.cs", 100),
        ("other source.cs", 200),
        ("other source.cs", 198),
    ] {
        assert!(
            visible.iter().any(
                |point| pdb.documents[point.document as usize - 1].ends_with(name)
                    && point.line == line
            ),
            "{name}:{line}"
        );
    }
    assert!(
        pdb.methods
            .iter()
            .flatten()
            .any(|point| point.source.is_none()),
        "hidden source range"
    );
    for points in &pdb.methods {
        assert!(
            points
                .windows(2)
                .all(|pair| pair[0].il_offset < pair[1].il_offset)
        );
    }
}
