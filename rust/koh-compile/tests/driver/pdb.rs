use super::{Host, gameboy_images, host};
use koh_compile::{assembly::Assembly, pdb::PortablePdb};
use rstest::rstest;

fn checksum_variants(image: &[u8]) -> [Vec<u8>; 3] {
    let file = object::read::pe::PeFile32::parse(image).unwrap();
    let data = file
        .data_directory(object::pe::IMAGE_DIRECTORY_ENTRY_DEBUG)
        .unwrap()
        .data(image, &file.section_table())
        .unwrap();
    let start = data.as_ptr() as usize - image.as_ptr() as usize;
    let field =
        |record: &[u8], offset| u32::from_le_bytes(record[offset..offset + 4].try_into().unwrap());
    let checksum = data
        .chunks_exact(28)
        .position(|p| field(p, 12) == 19)
        .unwrap()
        * 28;
    let spare = data
        .chunks_exact(28)
        .position(|p| field(p, 12) == 16)
        .unwrap()
        * 28;
    let original = &data[checksum..checksum + 28];
    let offset = field(original, 24) as usize;
    let size = field(original, 16) as usize;
    let mut wrong = image[offset..offset + size].to_vec();
    *wrong.last_mut().unwrap() ^= 1;
    let mut extra = original.to_vec();
    extra[20..24].copy_from_slice(&0u32.to_le_bytes());
    extra[24..28].copy_from_slice(&(image.len() as u32).to_le_bytes());
    let mut first = image.to_vec();
    first.extend(wrong);
    first[start + spare..start + spare + 28].copy_from_slice(&extra);
    let mut second = first.clone();
    second[start + checksum..start + checksum + 28].copy_from_slice(&extra);
    second[start + spare..start + spare + 28].copy_from_slice(original);
    let mut all_wrong = first.clone();
    all_wrong[offset + size - 1] ^= 1;
    [first, second, all_wrong]
}

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
    let pdb = PortablePdb::for_assembly(&bytes, &assembly).unwrap();
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
    let root = clrmeta::MetadataRoot::parse(&bytes).unwrap();
    let id = root.find_stream("#Pdb").unwrap().offset as usize;
    for byte in [0, 15, 16, 19] {
        let mut changed = bytes.clone();
        changed[id + byte] ^= 1;
        let error = PortablePdb::for_assembly(&changed, &assembly).unwrap_err();
        assert!(error.contains("identity"), "{error}");
    }
    let guids = root.find_stream("#GUID").unwrap();
    assert!(guids.size >= 16);
    let index = guids.offset as usize;
    let mut changed = bytes.clone();
    changed[index] ^= 1;
    let error = PortablePdb::for_assembly(&changed, &assembly).unwrap_err();
    assert!(error.contains("checksum"), "{error}");
    let variants = checksum_variants(&images[0]);
    for image in &variants[..2] {
        let assembly = Assembly::parse(image).unwrap();
        PortablePdb::for_assembly(&bytes, &assembly).unwrap();
    }
    let assembly = Assembly::parse(&variants[2]).unwrap();
    let error = PortablePdb::for_assembly(&bytes, &assembly).unwrap_err();
    assert!(error.contains("checksum"), "{error}");
    let library = Assembly::parse(&images[1]).unwrap();
    let library_pdb = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/Koh.GameBoy.pdb")),
    )
    .unwrap();
    let library_pdb = PortablePdb::for_assembly(&library_pdb, &library).unwrap();
    assert!(
        library_pdb
            .documents
            .iter()
            .any(|path| path.ends_with("TileSet.cs"))
    );
}
