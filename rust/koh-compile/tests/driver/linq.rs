use super::symbols::debugger;
use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn debugger_breaks_inside_a_linq_selector_and_unwinds(
    host: &Host,
    debugger: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    use serde_json::json;
    let (directory, images) = gameboy_images(host, configuration, "LinqQueries");
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let library = std::fs::read(output.join("GenericLibrary.dll")).unwrap();
    let framework = reference(directory.path(), configuration, "System.Runtime.dll");
    let linq = reference(directory.path(), configuration, "System.Linq.dll");
    let resolver = Resolver::new(&[&images[0], &images[1], &library, &framework, &linq]).unwrap();
    let root_pdb = std::fs::read(output.join("LinqQueries.pdb")).unwrap();
    let runtime_pdb = std::fs::read(output.join("Koh.GameBoy.pdb")).unwrap();
    let object = compile::object_with_debug(
        &resolver,
        Some("Program::DebugQuery"),
        &[Some(&root_pdb), Some(&runtime_pdb), None, None, None],
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
    let source = std::fs::read_to_string(directory.path().join("Program.cs")).unwrap();
    let line = source
        .lines()
        .position(|line| line.trim() == "return value * 2;")
        .unwrap()
        + 1;
    let point = linked
        .lines
        .iter()
        .find(|p| p.file.ends_with("Program.cs") && p.line as usize == line)
        .unwrap();
    let rom = directory.path().join("LINQ debug.gb");
    let debug = rom.with_extension("elf");
    std::fs::write(&rom, &linked.rom).unwrap();
    std::fs::write(&debug, linked.dwarf().unwrap()).unwrap();
    let mut requests = Vec::new();
    for (command, arguments) in [
        (
            "launch",
            json!({"program":rom,"debugInfo":debug,"hardwareMode":if color {"cgb"} else {"dmg"}}),
        ),
        (
            "setBreakpoints",
            json!({"source":{"path":point.file},"breakpoints":[{"line":line}]}),
        ),
        ("continue", json!({"threadId":1})),
        ("advance", json!({"frames":600})),
        ("stackTrace", json!({"threadId":1})),
        (
            "disassemble",
            json!({"memoryReference":format!("0x{:04X}",point.address),"instructionCount":4}),
        ),
    ] {
        requests.push(json!({"seq":requests.len()+1,"type":"request","command":command,"arguments":arguments}).to_string());
    }
    let replies = super::symbols::debugger_replies(debugger, &requests);
    for index in [0, 1, 2, 4, 5] {
        assert_eq!(replies[index]["success"], true, "{}", replies[index]);
    }
    assert_eq!(replies[1]["body"]["breakpoints"][0]["verified"], true);
    assert_eq!(replies[3]["reason"], "Breakpoint");
    assert_eq!(replies[3]["bootMapped"], false);
    assert_eq!(replies[3]["pc"], point.address);
    let frames = replies[4]["body"]["stackFrames"].as_array().unwrap();
    assert_eq!(frames[0]["source"]["path"], point.file);
    assert_eq!(frames[0]["line"], line);
    for name in ["Program::Project", "Program::DebugQuery"] {
        assert!(
            linked.symbols.iter().any(|s| s.name.contains(name)),
            "{name}"
        );
    }
    assert!(
        frames.iter().skip(1).any(|f| f["source"]["path"]
            .as_str()
            .is_some_and(|path| path.ends_with("NativeLinq.cs"))),
        "{frames:#?}"
    );
    let caller_line = source
        .lines()
        .position(|line| line.contains("Select(Project).Sum()"))
        .unwrap()
        + 1;
    assert!(
        frames
            .iter()
            .skip(1)
            .any(|f| f["source"]["path"] == point.file && f["line"] == caller_line),
        "{frames:#?}"
    );
    let instructions = replies[5]["body"]["instructions"].as_array().unwrap();
    assert_eq!(instructions.len(), 4);
    assert!(
        instructions
            .iter()
            .all(|i| !i["instructionBytes"].as_str().unwrap().is_empty())
    );
}

#[path = "../support/metadata.rs"]
mod metadata;

#[rstest]
#[case("Debug")]
#[case("Release")]
fn linq_metadata_contracts_and_debug_provenance(host: &Host, #[case] configuration: &str) {
    let (directory, images) = gameboy_images(host, configuration, "LinqQueries");
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let library = std::fs::read(output.join("GenericLibrary.dll")).unwrap();
    let framework = reference(directory.path(), configuration, "System.Runtime.dll");
    let linq = reference(directory.path(), configuration, "System.Linq.dll");
    for (before, after, expected) in [
        (
            "linq.sum.i32.array",
            "linq.sum.i32.zzzzz",
            "Missing KohRuntime linq.sum.i32.array",
        ),
        (
            "linq.min.i32.array",
            "linq.sum.i32.array",
            "Duplicate KohRuntime",
        ),
    ] {
        let mut changed = images[1].clone();
        let offsets: Vec<_> = changed
            .windows(before.len())
            .enumerate()
            .filter(|(_, b)| *b == before.as_bytes())
            .map(|(i, _)| i)
            .collect();
        assert_eq!(offsets.len(), 1);
        changed[offsets[0]..offsets[0] + before.len()].copy_from_slice(after.as_bytes());
        let resolver = Resolver::new(&[&images[0], &changed, &library, &framework, &linq]).unwrap();
        let error = compile::object(&resolver, Some("Program::SumsI32")).unwrap_err();
        assert!(error.contains(expected) && error.contains("IL_"), "{error}");
    }
    let resolver = Resolver::new(&[&images[0], &images[1], &library, &framework, &linq]).unwrap();
    let runtime = resolver.assembly(1).unwrap();
    let (row, method) = runtime
        .metadata()
        .method_defs
        .iter()
        .enumerate()
        .find(|(_, m)| runtime.metadata().strings.get(m.name).unwrap() == "SumI32Array")
        .unwrap();
    let mut renamed = images[1].clone();
    let names = super::float32::heap_offset(&renamed, "#Strings");
    renamed[names + method.name as usize] = b'Z';
    let renamed_resolver =
        Resolver::new(&[&images[0], &renamed, &library, &framework, &linq]).unwrap();
    compile::object(&renamed_resolver, Some("Program::SumsI32")).unwrap();
    let mut changed = images[1].clone();
    let signature = super::float32::heap_offset(&changed, "#Blob") + method.signature as usize;
    assert_eq!(&changed[signature..signature + 6], &[5, 0, 1, 8, 0x1d, 8]);
    changed[signature + 3] = 9;
    let changed_resolver =
        Resolver::new(&[&images[0], &changed, &library, &framework, &linq]).unwrap();
    let error = compile::object(&changed_resolver, Some("Program::SumsI32")).unwrap_err();
    assert!(
        error.contains("signature differs") && error.contains("IL_"),
        "{error}"
    );
    let mut changed = images[1].clone();
    let (offset, _) = metadata::table_row(&changed, clrmeta::TableId::MethodDef, row);
    let flags = u16::from_le_bytes(changed[offset + 6..offset + 8].try_into().unwrap()) & !0x10;
    changed[offset + 6..offset + 8].copy_from_slice(&flags.to_le_bytes());
    let changed_resolver =
        Resolver::new(&[&images[0], &changed, &library, &framework, &linq]).unwrap();
    let error = compile::object(&changed_resolver, Some("Program::SumsI32")).unwrap_err();
    assert!(
        error.contains("KohRuntime requires a static method")
            && error.contains("NativeLinq::SumI32Array")
            && error.contains("Assembly1"),
        "{error}"
    );
    let missing = Resolver::new(&[&images[0], &images[1], &library, &framework]).unwrap();
    let error = compile::object(&missing, Some("Program::Pipeline")).unwrap_err();
    assert!(
        error.contains("System.Linq") && error.contains("IL_"),
        "{error}"
    );
    for (name, entry, expected) in [
        ("WhereArray", "Pipeline", "Generic arity disagrees"),
        (
            "FailNull",
            "NullSource",
            "failnull requires an addressless static void() signature",
        ),
    ] {
        let method = runtime
            .metadata()
            .method_defs
            .iter()
            .find(|m| runtime.metadata().strings.get(m.name).unwrap() == name)
            .unwrap();
        let mut changed = images[1].clone();
        let signature = super::float32::heap_offset(&changed, "#Blob") + method.signature as usize;
        if name == "WhereArray" {
            assert_eq!(&changed[signature + 1..signature + 3], &[0x10, 1]);
            changed[signature + 2] = 2;
        } else {
            assert_eq!(&changed[signature..signature + 4], &[3, 0, 0, 1]);
            changed[signature + 3] = 8;
        }
        let changed_resolver =
            Resolver::new(&[&images[0], &changed, &library, &framework, &linq]).unwrap();
        let error =
            compile::object(&changed_resolver, Some(&format!("Program::{entry}"))).unwrap_err();
        assert!(
            error.contains(expected)
                && error.contains(if name == "WhereArray" { name } else { "IL_" }),
            "{error}"
        );
    }
    let root_pdb = std::fs::read(output.join("LinqQueries.pdb")).unwrap();
    let runtime_pdb = std::fs::read(output.join("Koh.GameBoy.pdb")).unwrap();
    let library_pdb = std::fs::read(output.join("GenericLibrary.pdb")).unwrap();
    let row = runtime
        .metadata()
        .method_defs
        .iter()
        .position(|m| runtime.metadata().strings.get(m.name).unwrap() == "FailNull")
        .unwrap();
    let mut changed = images[1].clone();
    let (offset, _) = metadata::table_row(&changed, clrmeta::TableId::MethodDef, row);
    changed[offset + 6] &= !0x10;
    let changed_resolver =
        Resolver::new(&[&images[0], &changed, &library, &framework, &linq]).unwrap();
    let error = compile::object(&changed_resolver, Some("Program::NullSource")).unwrap_err();
    assert!(error.contains("static") && error.contains("IL_"), "{error}");
    for (entry, value, foreign) in [("Pipeline", 24, false), ("Foreign", 42, true)] {
        let selected = format!("Program::{entry}");
        let object = compile::object_with_debug(
            &resolver,
            Some(&selected),
            &[
                Some(&root_pdb),
                Some(&runtime_pdb),
                Some(&library_pdb),
                None,
                None,
            ],
        )
        .unwrap();
        assert!(
            !object
                .sections
                .iter()
                .any(|s| s.name.starts_with(".text.__koh_a4_")
                    || (s.name.starts_with(".text.__koh_a3_")
                        && !s.name.starts_with(".text.__koh_a3_mfa")
                        && !s.name.starts_with(".text.__koh_a3_mfe"))),
            "reference assemblies supply dispatch declarations, never CIL bodies"
        );
        let bytes = koh_objects::write_elf(&object).unwrap();
        let fresh = Resolver::new(&[&images[0], &images[1], &library, &framework, &linq]).unwrap();
        assert_eq!(
            bytes,
            koh_objects::write_elf(
                &compile::object_with_debug(
                    &fresh,
                    Some(&selected),
                    &[
                        Some(&root_pdb),
                        Some(&runtime_pdb),
                        Some(&library_pdb),
                        None,
                        None
                    ]
                )
                .unwrap()
            )
            .unwrap()
        );
        let object = koh_objects::read_elf(&bytes).unwrap();
        let linked = koh_link::link(&[object], &Default::default()).unwrap();
        for file in ["Program.cs", "NativeLinq.cs"] {
            assert!(
                linked
                    .lines
                    .iter()
                    .any(|l| l.file.ends_with(file) && l.line > 0),
                "{entry}: {file}"
            );
        }
        assert_eq!(
            linked.lines.iter().any(|l| l.file.ends_with("Library.cs")),
            foreign,
            "{entry}"
        );
        assert!(linked.dwarf().unwrap().starts_with(b"\x7fELF"));
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(
            &fields[..4],
            &["00", &format!("{value:08X}"), "D000", "OK"],
            "{entry}"
        );
    }
}

fn reference(directory: &std::path::Path, configuration: &str, name: &str) -> Vec<u8> {
    let paths = std::fs::read_to_string(
        directory.join(format!("obj/{configuration}/net10.0/reference-paths.txt")),
    )
    .unwrap();
    std::fs::read(
        paths
            .lines()
            .find(|path| {
                std::path::Path::new(path)
                    .file_name()
                    .is_some_and(|file| file == name)
            })
            .unwrap(),
    )
    .unwrap()
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn linq_failures_and_unsupported_overloads_are_explicit(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "LinqQueries");
    let library = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericLibrary.dll")),
    )
    .unwrap();
    let framework = reference(directory.path(), configuration, "System.Runtime.dll");
    let linq = reference(directory.path(), configuration, "System.Linq.dll");
    let resolver = Resolver::new(&[&images[0], &images[1], &library, &framework, &linq]).unwrap();
    for (entry, operator) in [
        ("IndexedWhere", "Where"),
        ("IndexedSelect", "Select"),
        ("Materialize", "ToArray"),
        ("CustomComparer", "Max"),
    ] {
        let error = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap_err();
        assert!(
            error.contains("Unsupported LINQ") && error.contains(operator) && error.contains("IL_"),
            "{entry}: {error}"
        );
    }
    for (entry, code) in [
        ("NullSource", "04"),
        ("NullPredicate", "04"),
        ("NullSelector", "04"),
        ("EmptyMin", "0B"),
        ("EmptyMaxQuery", "0B"),
        ("OverflowI32", "02"),
        ("OverflowI64", "02"),
    ] {
        let object = compile::object(&resolver, Some(&format!("Program::{entry}")))
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
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_abort",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(fields[0], code, "{entry}: {report}");
        assert_eq!(fields[3], "OK", "{entry}");
        if entry == "NullSource" {
            super::assert_heap_snapshot(fields[4], &[0x5a; 4096], entry);
        }
        super::assert_heap_snapshot(&fields[4][0xf00 * 2..], &[0x5a; 256], entry);
        if color {
            assert_eq!(fields[5], "6B".repeat(4096), "{entry}");
        }
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn linq_deferred_queries_and_numeric_widths_execute(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "LinqQueries");
    let library = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericLibrary.dll")),
    )
    .unwrap();
    let framework = reference(directory.path(), configuration, "System.Runtime.dll");
    let linq = reference(directory.path(), configuration, "System.Linq.dll");
    let resolver = Resolver::new(&[&images[0], &images[1], &library, &framework, &linq]).unwrap();
    let ints = |values: &[i32]| {
        values
            .iter()
            .flat_map(|v| v.to_le_bytes())
            .collect::<Vec<_>>()
    };
    let mut cases = vec![
        (
            "MixedSelectors",
            [
                253000000000i64.to_le_bytes().as_slice(),
                126.5f64.to_le_bytes().as_slice(),
                253i32.to_le_bytes().as_slice(),
                30000000000i64.to_le_bytes().as_slice(),
                7i32.to_le_bytes().as_slice(),
                10000000000i64.to_le_bytes().as_slice(),
            ]
            .concat(),
        ),
        (
            "FloatEdges",
            [
                0x7fc00011u32,
                0x42280000,
                0x7fc00011,
                0x42280000,
                0x7fc00011,
                0xffc00022,
                0x7fc00011,
                0xffc00022,
                0x80000000,
                0x80000000,
                0x80000000,
                0x80000000,
            ]
            .iter()
            .flat_map(|v| v.to_le_bytes())
            .collect(),
        ),
        (
            "DoubleEdges",
            [
                0x7ff8000000000011u64,
                0x4045000000000000,
                0x7ff8000000000011,
                0x4045000000000000,
                0x7ff8000000000011,
                0xfff8000000000022,
                0x7ff8000000000011,
                0xfff8000000000022,
                0x8000000000000000,
                0x8000000000000000,
                0x8000000000000000,
                0x8000000000000000,
            ]
            .iter()
            .flat_map(|v| v.to_le_bytes())
            .collect(),
        ),
        (
            "NaNDisposal",
            ints(&[0x7fc00011, 2, 2, 1, 0x42280000, 3, 4, 1]),
        ),
        ("Escaped", ints(&[13, 2, 9])),
        (
            "Deferred",
            ints(&[
                0, 0, 0, 14, 4, 2, 1, 5, 1, 8, 8, 3, 2, 10, 2, 2, 9, 4, 3, 11, 3, 0, 3, 3,
            ]),
        ),
        (
            "ShortCircuit",
            ints(&[
                1, 2, 2, 1, 0, 3, 3, 1, 2, 4, 5, 1, 4, 0, 1, 1, 0, 1, 0, 0, 1, 0, 2, 1, 1,
            ]),
        ),
        (
            "Foreign",
            [
                20000000007i64.to_le_bytes().as_slice(),
                1i32.to_le_bytes().as_slice(),
            ]
            .concat(),
        ),
        ("SumsI32", ints(&[42, 42, 46, 46, 0, 0])),
        (
            "SumsI64",
            [
                0x123456780000002ai64,
                0x123456780000002a,
                0x123456780000002d,
                0x123456780000002d,
                0,
                0,
            ]
            .iter()
            .flat_map(|v| v.to_le_bytes())
            .collect(),
        ),
        (
            "SumsF32",
            [1f32, 1., 1., 1., 0., 0.]
                .iter()
                .flat_map(|v| v.to_le_bytes())
                .collect(),
        ),
        (
            "SumsF64",
            [0.625f64, 0.625, 0.3125, 0.3125, 0., 0.]
                .iter()
                .flat_map(|v| v.to_le_bytes())
                .collect(),
        ),
    ];
    for (entry, width, low, high) in [
        ("ExtremaI8", 1, (-100i64) as u64, 42),
        ("ExtremaU8", 1, 1, 250),
        ("ExtremaI16", 2, (-30000i64) as u64, 42),
        ("ExtremaU16", 2, 1, 60000),
        ("ExtremaI32", 4, i32::MIN as u64, i32::MAX as u64),
        ("ExtremaU32", 4, 0, u32::MAX as u64),
        ("ExtremaI64", 8, i64::MIN as u64, i64::MAX as u64),
        ("ExtremaU64", 8, 0, u64::MAX),
        ("ExtremaNative", 2, (-32768i64) as u64, 32767),
        ("ExtremaUNative", 2, 0, 65535),
        ("ExtremaF32", 4, 0xc0e00000, 0x42280000),
        ("ExtremaF64", 8, 0xc01c000000000000, 0x4045000000000000),
    ] {
        let pair = [&low.to_le_bytes()[..width], &high.to_le_bytes()[..width]].concat();
        cases.push((entry, pair.repeat(4)));
    }
    for (entry, expected) in cases {
        let object = compile::object(&resolver, Some(&format!("Program::{entry}")))
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
        .unwrap_or_else(|e| panic!("{entry}: {e}"));
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        let mut packet = [0x5a; 256];
        packet[..expected.len()].copy_from_slice(&expected);
        super::assert_heap_snapshot(&fields[4][0xf00 * 2..], &packet, entry);
        if color {
            assert_eq!(fields[5], "6B".repeat(4096), "{entry}");
        }
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn linq_pipeline_reductions_execute(host: &Host, #[case] configuration: &str, #[case] color: bool) {
    let (directory, images) = gameboy_images(host, configuration, "LinqQueries");
    let library = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/GenericLibrary.dll")),
    )
    .unwrap();
    let framework = reference(directory.path(), configuration, "System.Runtime.dll");
    let linq = reference(directory.path(), configuration, "System.Linq.dll");
    let resolver = Resolver::new(&[&images[0], &images[1], &library, &framework, &linq]).unwrap();
    for (entry, expected) in [("Pipeline", 24), ("Max", 9), ("PipelineMax", 3)] {
        let object = compile::object(&resolver, Some(&format!("Program::{entry}")))
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
            "--native-arena",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(
            &fields[..4],
            &["00", &format!("{expected:08X}"), "D000", "OK"],
            "{entry}"
        );
        assert_eq!(
            *fields.last().unwrap(),
            format!("{expected:02X}"),
            "{entry}: actual BGP"
        );
    }
}
