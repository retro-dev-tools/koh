use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn array_and_utf8_literals_preserve_data_and_logical_length(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "Literals");
    let references = std::fs::read_to_string(
        directory
            .path()
            .join(format!("obj/{configuration}/net10.0/reference-paths.txt")),
    )
    .unwrap();
    let runtime = references
        .lines()
        .map(std::path::Path::new)
        .find(|path| {
            path.file_name()
                .is_some_and(|name| name == "System.Runtime.dll")
        })
        .unwrap();
    let runtime = std::fs::read(runtime).unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1], &runtime]).unwrap();
    for (entry, expected, literal) in [
        ("ArrayValues", "00001637", &[11, 22, 33, 44, 55][..]),
        ("ArrayMutations", "00006316", &[11, 22, 33, 44, 55][..]),
        ("Utf8Values", "00004B21", &b"Koh!\0"[..]),
        ("Utf8Length", "00000004", &b"Koh!\0"[..]),
        (
            "Utf8Unicode",
            "00000689",
            &[0xc3, 0xa9, 0xf0, 0x9f, 0x90, 0x89, 0][..],
        ),
        ("Utf8Prefixes", "00000304", &b"Koh\0\0"[..]),
        ("Utf8Calls", "0000004B", &b"Koh!\0"[..]),
        ("Utf8Empty", "00000000", &[][..]),
        ("Utf8Default", "00000000", &[][..]),
    ] {
        let object = compile::object(&resolver, Some(&format!("Program::{entry}")))
            .unwrap_or_else(|error| panic!("{entry}: {error}"));
        if !literal.is_empty() {
            assert!(
                object
                    .sections
                    .iter()
                    .any(|section| section.name.starts_with(".rodata.")
                        && section
                            .data
                            .windows(literal.len())
                            .any(|bytes| bytes == literal)),
                "{entry} literal bytes"
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
            &["00", expected, "D000", "OK"],
            "{entry}: {report}"
        );
        let mut heap = [0x5a; 4096];
        if entry.starts_with("Array") {
            heap[..14].copy_from_slice(&[5, 0, 11, 22, 33, 44, 55, 5, 0, 11, 22, 33, 44, 55]);
            if entry == "ArrayMutations" {
                heap[3] = 99;
            }
        }
        super::assert_heap_snapshot(fields[4], &heap, entry);
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
    for entry in ["Utf8PastEnd", "Utf8Negative", "Utf8EmptyBounds"] {
        let object = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap();
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
        assert_eq!(fields[0], "06", "{entry}: {report}");
        assert_eq!(fields[3], "OK", "{entry}: {report}");
        super::assert_heap_snapshot(fields[4], &[0x5a; 4096], entry);
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
    for (entry, expected) in [
        ("Utf8Slice", "Unsupported readonly byte-span member: Slice"),
        (
            "WideSpan",
            "Only ReadOnlySpan<byte> literal views are supported",
        ),
        ("MutableSpan", "Mutable Span<T> execution is unsupported"),
    ] {
        let error = compile::object(&resolver, Some(&format!("Program::{entry}")))
            .err()
            .expect("Unsupported span operation must diagnose");
        assert!(
            error.contains(expected) && error.contains("IL_"),
            "{entry}: {error}"
        );
    }
}
