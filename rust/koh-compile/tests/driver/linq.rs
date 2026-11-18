use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

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
