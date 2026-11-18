use super::{Host, assert_heap_snapshot, execute_mode, gameboy_images, host};
use koh_compile::{
    compile,
    resolve::{Definition, Resolver},
    sm83,
};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn structured_finally_runs_on_normal_exits(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "FrameworkContracts");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let assembly = resolver.assembly(0).unwrap();
    let pdb_bytes = std::fs::read(directory.path().join(format!(
        "bin/{configuration}/net10.0/FrameworkContracts.pdb"
    )))
    .unwrap();
    koh_compile::pdb::PortablePdb::for_assembly(&pdb_bytes, assembly).unwrap();
    for row in 1..=assembly.metadata().method_defs.len() {
        let token = 0x06000000 | row as u32;
        if !assembly
            .method_name(token)
            .unwrap()
            .starts_with("FinallyRules::")
        {
            continue;
        }
        let body = assembly.method_body(token).unwrap();
        if body.regions.is_empty() || body.regions.iter().any(|r| r.flags != 2) {
            continue;
        }
        let function =
            koh_compile::lower::assembly_method(&resolver, Definition { assembly: 0, token })
                .unwrap();
        koh_compile::ir::verify(&function).unwrap();
        for block in &function.blocks {
            assert!(block.terminator.il_offset < body.il.len() as u32);
            for instruction in &block.instructions {
                assert!(instruction.il_offset < body.il.len() as u32);
            }
        }
    }
    let error = compile::object(&resolver, Some("FinallyRules::Catch")).unwrap_err();
    assert!(
        error.contains("Unsupported exception execution semantics: catch")
            && error.contains("FinallyRules::Catch"),
        "{error}"
    );
    for (entry, expected) in [
        ("Single", 12i32),
        ("EarlyReturn", 12),
        ("Nested", 1234),
        ("Break", 1727),
        ("Continue", 182838),
        ("CleanupInsideCleanup", 12345),
        ("Recursive", 321123),
        ("SavedReturn", 4),
        ("WideReturn", 7),
        ("Sequential", 1234),
    ] {
        let object = compile::object_with_debug(
            &resolver,
            Some(&format!("FinallyRules::{entry}")),
            &[Some(pdb_bytes.as_slice()), None],
        )
        .unwrap();
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                cgb: if color {
                    koh_link::CgbMode::Compatible
                } else {
                    koh_link::CgbMode::Preserve
                },
                ..Default::default()
            },
        )
        .unwrap();
        if entry == "Single" {
            let source = std::fs::read_to_string(directory.path().join("Program.cs")).unwrap();
            let line = source.lines().position(|s| s.trim() == "Mark(2);").unwrap() as u32 + 1;
            assert!(
                linked
                    .lines
                    .iter()
                    .any(|p| p.file.ends_with("Program.cs") && p.line == line)
            );
            assert!(linked.kdbg().unwrap().starts_with(b"KDBG"));
        }
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        if entry == "WideReturn" {
            assert_heap_snapshot(
                &fields[4][0xf08 * 2..0xf08 * 2 + 16],
                &0x123456780000002Au64.to_le_bytes(),
                entry,
            );
        }
        assert_heap_snapshot(
            &fields[4][0xf00 * 2..0xf00 * 2 + 8],
            &expected.to_le_bytes(),
            entry,
        );
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn framework_interface_without_base_binds(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "FrameworkContracts");
    let (facade, core) = runtime_images();
    let resolver = Resolver::new(&[&images[0], &images[1], &facade, &core]).unwrap();
    let object = compile::object(&resolver, Some("IteratorRules::InterfaceNull")).unwrap();
    let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
    let linked = koh_link::link(
        &[object],
        &koh_link::LinkOptions {
            cgb: if color {
                koh_link::CgbMode::Compatible
            } else {
                koh_link::CgbMode::Preserve
            },
            ..Default::default()
        },
    )
    .unwrap();
    let text = execute_mode(
        host,
        directory.path(),
        &linked,
        "__koh_exit",
        "--native-heap",
    );
    let fields: Vec<_> = text.split_whitespace().collect();
    assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"]);
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn roslyn_iterator_state_machines_execute(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "FrameworkContracts");
    let (facade, core) = runtime_images();
    let resolver = Resolver::new(&[&images[0], &images[1], &facade, &core]).unwrap();
    for (entry, expected) in [
        ("Sum", 10),
        ("Twice", 12),
        ("Repeat", 12),
        ("Nested", 9),
        ("EarlyBreak", 1),
        ("Generic", 42),
        ("ThreadId", 1),
        ("Empty", 0),
        ("WideValues", 42),
        ("DisposeNormal", 1),
        ("DisposeBreak", 1),
    ] {
        let object = compile::object(&resolver, Some(&format!("IteratorRules::{entry}")))
            .unwrap_or_else(|e| panic!("{entry}: {e}"));
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                cgb: if color {
                    koh_link::CgbMode::Compatible
                } else {
                    koh_link::CgbMode::Preserve
                },
                ..Default::default()
            },
        )
        .unwrap();
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(fields[0], "00", "{entry}: {text}");
        assert_eq!(fields[1], format!("{expected:08X}"), "{entry}");
        assert_eq!(&fields[2..4], &["D000", "OK"], "{entry}");
        if entry.starts_with("Dispose") {
            let expected = if entry == "DisposeNormal" { 3i32 } else { 1 };
            assert_heap_snapshot(
                &fields[4][0xf00 * 2..0xf00 * 2 + 8],
                &expected.to_le_bytes(),
                entry,
            );
        }
        if entry == "WideValues" {
            let expected: Vec<_> = [0x123456780000002Ai64, -7]
                .into_iter()
                .flat_map(i64::to_le_bytes)
                .collect();
            assert_heap_snapshot(&fields[4][0xf00 * 2..0xf00 * 2 + 32], &expected, entry);
        }
    }
}

fn runtime_images() -> (Vec<u8>, Vec<u8>) {
    let listing = super::dotnet().arg("--list-runtimes").output().unwrap();
    assert!(listing.status.success());
    let listing = String::from_utf8(listing.stdout).unwrap();
    let runtime = listing
        .lines()
        .find(|line| line.starts_with("Microsoft.NETCore.App 10."))
        .unwrap();
    let version = runtime.split_whitespace().nth(1).unwrap();
    let root = std::path::Path::new(runtime.rsplit_once('[').unwrap().1.trim_end_matches(']'))
        .join(version);
    let facade = std::fs::read(root.join("System.Runtime.dll")).unwrap();
    let core = std::fs::read(root.join("System.Private.CoreLib.dll")).unwrap();
    (facade, core)
}

fn ready_to_run_offsets(bytes: &[u8]) -> (usize, usize) {
    macro_rules! offsets {
        ($image:ty) => {{
            let file = <$image>::parse(bytes).unwrap();
            let sections = file.section_table();
            let cli = file
                .data_directory(object::pe::IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR)
                .unwrap()
                .data(bytes, &sections)
                .unwrap();
            let rva = u32::from_le_bytes(cli[64..68].try_into().unwrap());
            (
                cli.as_ptr() as usize - bytes.as_ptr() as usize,
                sections.pe_file_range_at(rva).unwrap().0 as usize,
            )
        }};
    }
    match object::FileKind::parse(bytes).unwrap() {
        object::FileKind::Pe32 => offsets!(object::read::pe::PeFile32),
        object::FileKind::Pe64 => offsets!(object::read::pe::PeFile64),
        _ => panic!("Expected CLI PE fixture"),
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn resolved_bcl_integer_max_has_correct_semantics(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "FrameworkContracts");
    let (facade, core) = runtime_images();
    let (cli, header) = ready_to_run_offsets(&core);
    for (offset, bytes, diagnostic) in [
        (cli + 16, vec![0x1c, 0, 0, 0], "Native-entry"),
        (cli + 64, vec![0, 0, 0, 0], "Mixed-mode"),
        (cli + 68, 15u32.to_le_bytes().to_vec(), "Mixed-mode"),
        (
            cli + 68,
            u32::MAX.to_le_bytes().to_vec(),
            "Truncated or overflowing image range",
        ),
        (header, vec![0, 0, 0, 0], "Mixed-mode"),
        (
            header + 4,
            15u16.to_le_bytes().to_vec(),
            "Unsupported ReadyToRun major version",
        ),
        (header + 8, 0x20u32.to_le_bytes().to_vec(), "Composite"),
        (header + 8, 0x10u32.to_le_bytes().to_vec(), "embedded-MSIL"),
        (
            header + 12,
            u32::MAX.to_le_bytes().to_vec(),
            "Truncated ReadyToRun section directory",
        ),
    ] {
        let mut changed = core.clone();
        changed[offset..offset + bytes.len()].copy_from_slice(&bytes);
        let error = koh_compile::assembly::Assembly::parse(&changed).unwrap_err();
        assert!(error.contains(diagnostic), "{error}");
    }
    let resolver = Resolver::new(&[&images[0], &images[1], &facade, &core]).unwrap();
    let error = compile::object(&resolver, Some("ReferenceRules::BclNative")).unwrap_err();
    assert!(
        error.contains("Sqrt") && error.contains("no managed CIL body"),
        "{error}"
    );
    for (entry, expected) in [
        ("ReferenceRules::BclMax", "00000005"),
        ("ReferenceRules::BclMaxEdges", "0000002A"),
    ] {
        let object = compile::object(&resolver, Some(entry))
            .unwrap_or_else(|error| panic!("{entry}: {error}"));
        let core_methods: Vec<_> = object
            .symbols
            .iter()
            .filter(|symbol| {
                symbol.name.starts_with("__koh_a3_m")
                    && !symbol.name.contains('.')
                    && matches!(
                        symbol.definition,
                        koh_objects::SymbolDefinition::Label { .. }
                    )
            })
            .collect();
        assert!(
            core_methods.len() <= 1,
            "Only the reached Math.Max body may be emitted"
        );
        for symbol in core_methods {
            let token =
                u32::from_str_radix(symbol.name.strip_prefix("__koh_a3_m").unwrap(), 16).unwrap();
            assert_eq!(
                resolver.assembly(3).unwrap().method_name(token).unwrap(),
                "System.Math::Max"
            );
        }
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                cgb: if color {
                    koh_link::CgbMode::Compatible
                } else {
                    koh_link::CgbMode::Preserve
                },
                ..Default::default()
            },
        )
        .unwrap();
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", expected, "D000", "OK"]);
        if entry.ends_with("Edges") {
            let expected: Vec<_> = [-3i32, i32::MAX, i32::MAX, 0, 0]
                .into_iter()
                .flat_map(i32::to_le_bytes)
                .collect();
            assert_heap_snapshot(&fields[4][0xf00 * 2..0xf00 * 2 + 40], &expected, entry);
        }
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn string_tables_and_reached_library_memory_preserve_values(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "FrameworkContracts");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, fault) in [
        ("ReferenceRules::Memory", None),
        ("ReferenceRules::SingleCell", None),
        ("ReferenceRules::Clear", None),
        ("StringTables::Values", None),
        ("StringTables::NullElement", Some("04")),
        ("StringTables::EndIndex", Some("06")),
        ("StringTables::NegativeIndex", Some("06")),
    ] {
        let object = compile::object(&resolver, Some(entry)).unwrap();
        for (assembly, name) in [(0, "Unreached"), (1, "TryEnableDoubleSpeed")] {
            let data = resolver.assembly(assembly).unwrap().metadata();
            let row = data
                .method_defs
                .iter()
                .position(|m| data.strings.get(m.name).unwrap() == name)
                .unwrap();
            let absent = sm83::symbol(Definition {
                assembly,
                token: 0x06000001 + row as u32,
            });
            assert!(
                !object.symbols.iter().any(|s| s.name == absent),
                "{entry}: uncalled {name}"
            );
        }
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                cgb: if color {
                    koh_link::CgbMode::Compatible
                } else {
                    koh_link::CgbMode::Preserve
                },
                ..Default::default()
            },
        )
        .unwrap();
        let video = matches!(
            entry,
            "ReferenceRules::SingleCell" | "ReferenceRules::Clear"
        );
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            if fault.is_some() {
                "__koh_abort"
            } else {
                "__koh_exit"
            },
            if video {
                "--native-maps"
            } else {
                "--native-heap"
            },
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        if let Some(fault) = fault {
            assert_eq!(fields[0], fault, "{entry}: fault packet {:?}", &fields[..4]);
            assert_eq!(fields[3], "OK", "{entry}: reserved-memory guard");
            continue;
        }
        assert_eq!(
            &fields[..4],
            &["00", "0000002A", "D000", "OK"],
            "{entry}: {text}"
        );
        if video {
            let mut vram = vec![0xa7; 8192];
            vram[0x1800..0x1c00].fill(if entry.ends_with("Clear") { 7 } else { 0 });
            if entry.ends_with("SingleCell") {
                vram[0x1800 + 101] = 9;
            }
            assert_heap_snapshot(fields[if color { 6 } else { 5 }], &vram, entry);
            if color {
                assert_heap_snapshot(fields[7], &[0xb8; 8192], "unmodified VRAM1");
            }
        } else {
            let expected = if entry.ends_with("Memory") {
                (1..=8).chain([0x7a; 8]).chain([0x1b]).collect::<Vec<u8>>()
            } else {
                vec![3, b'H', b'W', b'G', 6, 2, b'C', 3, b'Z', 1]
            };
            assert_heap_snapshot(
                &fields[4][0xf00 * 2..(0xf00 + expected.len()) * 2],
                &expected,
                entry,
            );
            assert_heap_snapshot(
                &fields[4][(0xf00 + expected.len()) * 2..],
                &vec![0x5a; 256 - expected.len()],
                "packet extent",
            );
        }
    }
}
