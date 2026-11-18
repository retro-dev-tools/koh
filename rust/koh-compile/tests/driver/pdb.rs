use super::{Host, execute, gameboy_images, host};
use koh_compile::{assembly::Assembly, compile, pdb::PortablePdb, resolve::Resolver};
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

#[rstest]
#[case("Debug")]
#[case("Release")]
fn source_ranges_survive_elf_linking_and_preserve_rom_execution(
    host: &Host,
    #[case] configuration: &str,
) {
    let (directory, images) = gameboy_images(host, configuration, "SourceDebug");
    let symbols: Vec<_> = ["SourceDebug", "Koh.GameBoy"]
        .map(|name| {
            std::fs::read(
                directory
                    .path()
                    .join(format!("bin/{configuration}/net10.0/{name}.pdb")),
            )
            .unwrap()
        })
        .into();
    let views: Vec<_> = images.iter().map(Vec::as_slice).collect();
    let resolver = Resolver::new(&views).unwrap();
    let symbol_views: Vec<_> = symbols.iter().map(|bytes| Some(bytes.as_slice())).collect();
    let object = compile::object_with_debug(&resolver, None, &symbol_views).unwrap();
    let encoded = koh_objects::write_elf(&object).unwrap();
    let object = koh_objects::read_elf(&encoded).unwrap();
    let lines: Vec<_> = object.sections.iter().flat_map(|s| &s.lines).collect();
    for (file, line) in [
        ("mapped source.cs", 100),
        ("other source.cs", 200),
        ("other source.cs", 198),
    ] {
        assert!(
            lines
                .iter()
                .any(|p| p.file.ends_with(file) && p.line == line && p.count != 0),
            "{file}:{line}"
        );
    }
    assert!(
        lines.iter().any(|p| p.file.ends_with("Rgb.cs")),
        "referenced runtime source"
    );
    assert!(
        object
            .symbols
            .iter()
            .any(|s| s.name.contains("Program::Helper")),
        "readable function symbol"
    );
    let root_pdb = PortablePdb::for_assembly(&symbols[0], resolver.assembly(0).unwrap()).unwrap();
    let helper = resolver
        .assembly(0)
        .unwrap()
        .metadata()
        .method_defs
        .iter()
        .enumerate()
        .find(|(_, method)| {
            resolver
                .assembly(0)
                .unwrap()
                .metadata()
                .strings
                .get(method.name)
                .unwrap()
                == "Helper"
        })
        .map(|(index, _)| 0x06000001 + index as u32)
        .unwrap();
    let prefix = format!(
        "{}.IL_",
        koh_compile::sm83::symbol(koh_compile::resolve::Definition {
            assembly: 0,
            token: helper
        })
    );
    let markers: Vec<_> = object
        .symbols
        .iter()
        .filter_map(|symbol| {
            let suffix = symbol.name.strip_prefix(&prefix)?;
            let offset = u32::from_str_radix(suffix.split('.').next()?, 16).unwrap();
            let koh_objects::SymbolDefinition::Label {
                section,
                offset: machine,
            } = symbol.definition
            else {
                return None;
            };
            Some((section, machine as usize, offset))
        })
        .collect();
    let mut hidden_bytes = 0;
    for pair in markers.windows(2) {
        let (section, start, il) = pair[0];
        let (next_section, end, _) = pair[1];
        assert_eq!(section, next_section);
        if root_pdb.source(helper, il).is_none() && start < end {
            hidden_bytes += end - start;
            assert!(
                object.sections[section.0]
                    .lines
                    .iter()
                    .all(|line| line.offset + line.count <= start || line.offset >= end),
                "hidden IL{il:04x} has a source mapping"
            );
        }
    }
    assert!(hidden_bytes > 0, "actual emitted hidden code");
    let (section, first, _) = markers[0];
    assert!(first > 0, "compiler prologue");
    assert!(
        object.sections[section.0]
            .lines
            .iter()
            .all(|line| line.offset >= first)
    );
    let linked = koh_link::link(&[object], &Default::default()).unwrap();
    assert!(
        linked
            .lines
            .iter()
            .any(|p| p.file.ends_with("mapped source.cs") && p.line == 100)
    );
    assert!(linked.dwarf().unwrap().starts_with(b"\x7fELF"));
    let plain = compile::object(&resolver, None).unwrap();
    assert!(plain.sections.iter().all(|s| s.lines.is_empty()));
    let plain = koh_link::link(&[plain], &Default::default()).unwrap();
    assert_eq!(
        linked.rom, plain.rom,
        "source metadata must not change execution bytes"
    );
    let output = execute(host, directory.path(), &linked, "__koh_exit");
    assert_eq!(
        &output.split_whitespace().collect::<Vec<_>>()[..4],
        &["00", "0000002A", "D000", "OK"]
    );
    for (inputs, expected) in [
        ([Some(symbols[0].as_slice()), None], "mapped source.cs"),
        ([None, Some(symbols[1].as_slice())], "Rgb.cs"),
    ] {
        let object = compile::object_with_debug(&resolver, None, &inputs).unwrap();
        assert!(object.sections.iter().flat_map(|s| &s.lines).all(|line| {
            line.file.ends_with(expected)
                || (expected == "mapped source.cs"
                    && (line.file.ends_with("other source.cs")
                        || line.file.ends_with("Program.cs")))
        }));
        assert!(object.sections.iter().any(|s| !s.lines.is_empty()));
    }
    assert!(compile::object_with_debug(&resolver, None, &[None]).is_err());
    assert!(
        compile::object_with_debug(&resolver, None, &[Some(&symbols[1]), Some(&symbols[0])])
            .is_err()
    );
}
