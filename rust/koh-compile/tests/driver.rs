use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;
use std::path::{Path, PathBuf};
#[path = "driver/delegates.rs"]
mod delegates;
#[path = "driver/mono.rs"]
mod mono;
#[path = "driver/pdb.rs"]
mod pdb;
#[path = "driver/rectangular.rs"]
mod rectangular;
mod support;
#[path = "driver/symbols.rs"]
mod symbols;
#[path = "driver/vblank.rs"]
mod vblank;
#[path = "driver/vram.rs"]
mod vram;
use support::{Host, dotnet};

#[rstest::fixture]
#[once]
fn host() -> Host {
    support::build_host()
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn managed_value_copies_share_references_and_preserve_fields(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "ManagedValues");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for entry in ["Main", "AssetCount", "RecursiveArray", "ReferenceAliases"] {
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
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(
            &fields[..4],
            &["00", "0000002A", "D000", "OK"],
            "{entry}: {report}"
        );
        if entry == "Main" {
            let mut expected = 12u16.to_le_bytes().to_vec();
            for value in [13i32, 99, 7, 8, 2, 79, 17, 99, 27, 13, 11, 8] {
                expected.extend(value.to_le_bytes());
            }
            assert_heap_snapshot(&fields[4][..expected.len() * 2], &expected, entry);
        }
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
}

#[rstest]
#[case("Debug", false, 1)]
#[case("Release", false, 257)]
#[case("Debug", true, 257)]
#[case("Release", true, 1)]
fn virtual_slots_and_constructor_dispatch_execute(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
    #[case] first_bank: i32,
) {
    let (directory, images) = gameboy_images(host, configuration, "VirtualCalls");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, fault) in [
        ("Main", 0),
        ("Slots", 0),
        ("Constructor", 0),
        ("Null", 4),
        ("Transitive", 0),
        ("Cast", 0),
        ("InvalidCast", 10),
        ("NullCast", 0),
        ("Wide", 0),
    ] {
        let mut object = compile::object(&resolver, Some(&format!("Program::{entry}")))
            .unwrap_or_else(|e| panic!("{entry}: {e}"));
        let metadata = resolver.assembly(0).unwrap();
        let unused = (1..=metadata.metadata().method_defs.len())
            .find(|&row| metadata.method_name(0x06000000 | row as u32).unwrap() == "Unused::Area")
            .unwrap();
        let unused = koh_compile::sm83::symbol(koh_compile::resolve::Definition {
            assembly: 0,
            token: 0x06000000 | unused as u32,
        });
        assert!(
            !object.symbols.iter().any(|s| s.name == unused),
            "uninstantiated override must remain dead"
        );
        let mut bank = first_bank;
        for section in &mut object.sections {
            if section.name.starts_with(".text.__koh_a") {
                assert_eq!(section.kind, 1);
                section.bank = Some(bank);
                bank += 1;
            }
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
            if fault == 0 {
                "__koh_exit"
            } else {
                "__koh_abort"
            },
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(fields[0], format!("{fault:02X}"), "{entry}: {report}");
        assert_eq!(fields[3], "OK", "{entry}: frame guards");
        if entry == "Main" {
            let mut expected = 8u16.to_le_bytes().to_vec();
            for value in [18i32, 0xe3, 12, 0x82, 4, 0xa3, 0, 0] {
                expected.extend(value.to_le_bytes());
            }
            assert_heap_snapshot(&fields[4][..expected.len() * 2], &expected, entry);
        }
        if fault == 0 {
            assert_eq!((fields[1], fields[2]), ("0000002A", "D000"), "{entry}");
        }
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn malformed_virtual_overrides_are_diagnostics(host: &Host, #[case] configuration: &str) {
    let (_directory, images) = gameboy_images(host, configuration, "VirtualCalls");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let image = resolver.assembly(0).unwrap();
    let unsupported = compile::object(&resolver, Some("Program::Covariant"))
        .err()
        .expect("Explicit MethodImpl must diagnose until implemented");
    assert!(
        unsupported.contains("MethodImpl") && unsupported.contains("Program::Covariant IL_"),
        "{unsupported}"
    );
    let method = |name| {
        (1..=image.metadata().method_defs.len())
            .map(|row| 0x06000000 | row as u32)
            .find(|&token| image.method_name(token).unwrap() == name)
            .unwrap()
    };
    let position = |token| {
        let row = image.method(token).unwrap();
        let mut needle = row.rva.to_le_bytes().to_vec();
        needle.extend(row.impl_flags.to_le_bytes());
        needle.extend(row.flags.to_le_bytes());
        needle.extend(u16::try_from(row.name).unwrap().to_le_bytes());
        needle.extend(u16::try_from(row.signature).unwrap().to_le_bytes());
        let matches: Vec<_> = images[0]
            .windows(needle.len())
            .enumerate()
            .filter_map(|(at, bytes)| (bytes == needle).then_some(at))
            .collect();
        assert_eq!(matches.len(), 1);
        matches[0]
    };
    for (kind, entry, expected) in [
        ("final", "Slots", "final"),
        ("duplicate", "Main", "Ambiguous"),
    ] {
        let mut changed = images[0].clone();
        if kind == "final" {
            let token = method("First::Value");
            let at = position(token);
            changed[at + 6..at + 8]
                .copy_from_slice(&(image.method(token).unwrap().flags | 0x20).to_le_bytes());
        } else {
            let token = method("Square::Kind");
            let at = position(token);
            let area = image.method(method("Square::Area")).unwrap();
            changed[at + 8..at + 10]
                .copy_from_slice(&u16::try_from(area.name).unwrap().to_le_bytes());
            changed[at + 10..at + 12]
                .copy_from_slice(&u16::try_from(area.signature).unwrap().to_le_bytes());
            let parsed = koh_compile::assembly::Assembly::parse(&changed).unwrap();
            assert_eq!(parsed.method(token).unwrap().name, area.name);
            assert_eq!(
                parsed.method_signature(token).unwrap(),
                image.method_signature(method("Square::Area")).unwrap()
            );
        }
        let error = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let resolver = Resolver::new(&[&changed, &images[1]])?;
            compile::object(&resolver, Some(&format!("Program::{entry}")))
        }))
        .expect("Malformed virtual metadata must not panic")
        .err()
        .expect("Malformed overrides must diagnose");
        assert!(
            error.contains(expected) && error.contains(&format!("Program::{entry} IL_")),
            "{kind}: {error}"
        );
    }
}

#[test]
fn virtual_binding_work_limit_is_diagnostic() {
    let directory = tempfile::Builder::new()
        .prefix("koh virtual work ")
        .tempdir()
        .unwrap();
    let project = directory.path().join("Work.csproj");
    std::fs::write(&project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><TreatWarningsAsErrors>true</TreatWarningsAsErrors><NuGetAudit>true</NuGetAudit></PropertyGroup></Project>").unwrap();
    let mut source = String::from("public class Root {");
    for index in 0..80 {
        source.push_str(&format!("public virtual int M{index}() => 0;"));
    }
    source.push('}');
    for index in 0..80 {
        source.push_str(&format!("public sealed class C{index} : Root {{}}"));
    }
    source
        .push_str("public class Program { public static int Main() { Root current; int total = 0;");
    for index in 0..80 {
        source.push_str(&format!(
            "current = new C{index}(); total += current.M{index}();"
        ));
    }
    source.push_str("return total; }}");
    std::fs::write(directory.path().join("Program.cs"), source).unwrap();
    let result = dotnet()
        .arg("build")
        .arg(&project)
        .args(["-c", "Release", "--nologo", "-v:q", "-m:1"])
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}{}",
        String::from_utf8_lossy(&result.stdout),
        String::from_utf8_lossy(&result.stderr)
    );
    let image = std::fs::read(directory.path().join("bin/Release/net10.0/Work.dll")).unwrap();
    let error = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let resolver = Resolver::new(&[&image])?;
        compile::object(&resolver, None)
    }))
    .expect("Virtual work bound must not panic")
    .unwrap_err();
    assert!(
        error.contains("Virtual binding work exceeds1048576")
            && error.contains("Program::Main IL_"),
        "{error}"
    );
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn native_object_identity_fields_and_faults(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "ObjectCore");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (name, fault) in [
        ("Main", 0),
        ("NullCall", 4),
        ("NullField", 4),
        ("Exhaust", 5),
        ("MergeTrue", 0),
        ("MergeFalse", 0),
        ("NullMerge", 0),
        ("ObjectMerge", 0),
        ("Recursive", 0),
        ("FieldReference", 0),
        ("ArrayOfObjects", 0),
        ("NullElement", 4),
        ("BannerFields", 0),
        ("InheritedFields", 0),
        ("StaticReference", 0),
    ] {
        let object = compile::object(&resolver, Some(&format!("Program::{name}")))
            .unwrap_or_else(|e| panic!("{name}: {e}"));
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
            if fault == 0 {
                "__koh_exit"
            } else {
                "__koh_abort"
            },
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(fields.len(), if color { 6 } else { 5 }, "{name}: {report}");
        assert_eq!(fields[0], format!("{fault:02X}"), "{name}: {report}");
        assert_eq!(fields[3], "OK", "{name}: stack guards");
        if fault == 0 {
            assert_eq!((fields[1], fields[2]), ("0000002A", "D000"), "{report}");
        }
        let descriptor_for = |owner: &str| {
            let metadata = resolver.assembly(0).unwrap().metadata();
            let index = metadata
                .type_defs
                .iter()
                .position(|t| metadata.strings.get(t.type_name).unwrap() == owner)
                .unwrap();
            let name = koh_compile::sm83::class_symbol(Some(koh_compile::resolve::Definition {
                assembly: 0,
                token: 0x02000001 + index as u32,
            }));
            u16::try_from(
                linked
                    .symbols
                    .iter()
                    .find(|s| s.name == name)
                    .unwrap()
                    .value,
            )
            .unwrap()
        };
        if name == "Main" {
            let symbol = |name: &str| {
                u16::try_from(
                    linked
                        .symbols
                        .iter()
                        .find(|s| s.name == name)
                        .unwrap()
                        .value,
                )
                .unwrap()
            };
            let metadata = resolver.assembly(0).unwrap().metadata();
            let index = metadata
                .type_defs
                .iter()
                .position(|t| metadata.strings.get(t.type_name).unwrap() == "Box")
                .unwrap();
            let descriptor = symbol(&koh_compile::sm83::class_symbol(Some(
                koh_compile::resolve::Definition {
                    assembly: 0,
                    token: 0x02000001 + index as u32,
                },
            )));
            let parent = descriptor_for("Parent");
            let root = symbol(&koh_compile::sm83::class_symbol(None));
            for (address, value) in [(descriptor, parent), (parent, root), (root, 0)] {
                assert_eq!(
                    &linked.rom[usize::from(address)..usize::from(address) + 2],
                    value.to_le_bytes()
                );
            }
            let text = symbol("__koh_string_0");
            let mut expected = [0x5a; 4096];
            expected[..34].fill(0);
            for (offset, value, extra, bytes, next) in [
                (0usize, 13u32, 1u8, 0xd00du16, 0xd011u16),
                (17, 20, 0, 0xd01e, 0),
            ] {
                expected[offset..offset + 2].copy_from_slice(&descriptor.to_le_bytes());
                expected[offset + 2] = extra;
                expected[offset + 3..offset + 7].copy_from_slice(&value.to_le_bytes());
                expected[offset + 7..offset + 9].copy_from_slice(&text.to_le_bytes());
                expected[offset + 9..offset + 11].copy_from_slice(&bytes.to_le_bytes());
                expected[offset + 11..offset + 13].copy_from_slice(&next.to_le_bytes());
            }
            expected[13] = 2;
            expected[16] = 2;
            expected[30] = 2;
            expected[33] = 2;
            assert_heap_snapshot(fields[4], &expected, name);
        }
        if matches!(name, "BannerFields" | "InheritedFields") {
            let mut expected = [0x5a; 4096];
            let packet: [u8; 16] = if name == "BannerFields" {
                expected[..12].fill(0);
                let descriptor = descriptor_for("Banner");
                for (offset, label, score) in
                    [(0, "__koh_string_1", 2048u16), (6, "__koh_string_0", 512)]
                {
                    let text = u16::try_from(
                        linked
                            .symbols
                            .iter()
                            .find(|s| s.name == label)
                            .unwrap()
                            .value,
                    )
                    .unwrap();
                    expected[offset..offset + 2].copy_from_slice(&descriptor.to_le_bytes());
                    expected[offset + 2..offset + 4].copy_from_slice(&text.to_le_bytes());
                    expected[offset + 4..offset + 6].copy_from_slice(&score.to_le_bytes());
                }
                [8, 9, b'Y', b'W', b'G', b'O', 0, 8, 0, 2, 0, 0, 0, 0, 0, 0]
            } else {
                expected[..2].copy_from_slice(&descriptor_for("Boss").to_le_bytes());
                expected[2..8].copy_from_slice(&[77, 0x88, 0x13, 3, 0xbc, 2]);
                [
                    42, 0x88, 0x13, 3, 0xbc, 2, 42, 0x88, 0x13, 77, 3, 0, 0, 0, 0, 0,
                ]
            };
            expected[4080..].copy_from_slice(&packet);
            assert_heap_snapshot(fields[4], &expected, name);
        }
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], name);
        }
        if matches!(name, "NullCall" | "NullField") {
            assert_heap_snapshot(fields[4], &[0x5a; 4096], name);
        }
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn hblank_stream_boundaries_and_hardware_state(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "HBlank");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for shape in ["Register", "Return", "Pointer", "Count"] {
        let error =
            compile::object(&resolver, Some(&format!("Program::Invalid{shape}"))).unwrap_err();
        assert!(
            error.contains("Invalid Koh hblankstream intrinsic contract") && error.contains("IL_"),
            "{error}"
        );
    }
    for (name, first, count, fault, ime) in [
        ("Zero", 255, 0, 0, 1),
        ("ZeroOn", 144, 0, 0, 0),
        ("First", 0, 1, 0, 0),
        ("Last", 143, 1, 0, 0),
        ("Full", 0, 144, 0, 0),
        ("Cube", 39, 64, 0, 0),
        ("Double", 0, 144, 0, 0),
        ("BadFirst", 144, 1, 6, 0),
        ("BadWrap", 255, 1, 6, 0),
        ("BadEnd", 143, 2, 6, 0),
        ("Off", 0, 1, 9, 0),
    ] {
        if name == "Double" && !color {
            continue;
        }
        let entry = format!("Program::{name}");
        let object = compile::object(&resolver, Some(&entry)).unwrap();
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
            if fault == 0 {
                "__koh_exit"
            } else {
                "__koh_abort"
            },
            "--native-raster",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(fields.len(), if color { 9 } else { 8 }, "{entry}: {text}");
        assert_eq!(fields[0], format!("{fault:02X}"), "{entry}: {text}");
        assert_eq!(fields[3], "OK", "{entry}: stack guard");
        if fault == 0 {
            assert_eq!((fields[1], fields[2]), ("0000002A", "D000"), "{entry}");
        }
        for bank in 0..if color { 2 } else { 1 } {
            let mut memory = [if bank == 0 { 0x5a } else { 0x6b }; 4096];
            if !color || bank == 1 {
                for (i, value) in memory[0x100..0x190].iter_mut().enumerate() {
                    *value = i as u8 + 1;
                }
            }
            assert_heap_snapshot(fields[4 + bank], &memory, &entry);
        }
        let state = if color { 6 } else { 5 };
        let control: Vec<_> = fields[state].split(':').collect();
        assert_eq!(control[0], ime.to_string(), "{entry}: IME");
        assert_eq!(
            control[2],
            if name == "Double" { "1" } else { "0" },
            "{entry}: speed"
        );
        if color {
            assert_eq!(
                u8::from_str_radix(control[1], 16).unwrap() & 7,
                2,
                "{entry}: WRAM bank"
            );
        }
        let performed = if fault == 0 { count as usize } else { 0 };
        let expected_reads = (0..performed)
            .map(|i| format!("{:04X}:{:02X}", 0xd100 + i, i + 1))
            .collect::<Vec<_>>();
        assert_eq!(
            fields[state + 1],
            if performed == 0 {
                "-".into()
            } else {
                expected_reads.join(",")
            },
            "{entry}: source reads"
        );
        if performed == 0 {
            assert_eq!(fields[state + 2], "-", "{entry}: display writes");
        } else {
            let writes: Vec<_> = fields[state + 2].split(',').collect();
            assert_eq!(writes.len(), performed, "{entry}");
            for (i, write) in writes.iter().enumerate() {
                let value: Vec<u32> = write.split(':').map(|s| s.parse().unwrap()).collect();
                assert_eq!(
                    (value[0], value[2], value[3]),
                    (first + i as u32, 0, i as u32 + 1),
                    "{entry}: {write}"
                );
                assert!(value[1] < 456, "{entry}: {write}");
            }
        }
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn vblank_wait_observes_first_window_at_varied_entry_phases(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "VBlank");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for phase in 0..9 {
        let entry = if phase == 8 {
            "Program::Twice".into()
        } else {
            format!("Program::Phase{phase}")
        };
        let object = compile::object(&resolver, Some(&entry)).unwrap();
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
            "--native-heap-cost",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(fields.len(), if color { 7 } else { 6 });
        assert_eq!(
            (fields[0], fields[2], fields[3]),
            ("00", "D000", "OK"),
            "{entry}"
        );
        let line = u32::from_str_radix(fields[1], 16).unwrap();
        let cycles: u64 = fields.last().unwrap().parse().unwrap();
        println!("{configuration} color={color} phase={phase}: LY={line} {cycles}T");
        assert!(
            (144..=147).contains(&line),
            "{entry}: returned late/outside early VBlank at LY={line}"
        );
        if phase == 8 {
            assert!(
                (130000..160000).contains(&cycles),
                "{entry}: {cycles}T must wait for two distinct windows"
            );
        } else {
            assert!(
                cycles < 90000,
                "{entry}: {cycles}T missed the first VBlank window"
            );
        }
        assert_heap_snapshot(fields[4], &[0x5a; 4096], &entry);
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], &entry);
        }
    }
}

#[rstest]
#[case("Debug", true)]
#[case("Release", true)]
#[case("Debug", false)]
#[case("Release", false)]
fn direct_gdma_maximum_preserves_every_byte_at_varied_phases(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "Gdma");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let entries = (0..8).map(|phase| (format!("Phase{phase}"), 2048)).chain(
        [
            ("Zero", 0),
            ("On16", 16),
            ("On1024", 1024),
            ("On1040", 1040),
            ("On1920", 1920),
            ("Off2048", 2048),
        ]
        .map(|(name, count)| (name.into(), count)),
    );
    for (name, count) in entries {
        let entry = format!("Program::{name}");
        let object = compile::object(&resolver, Some(&entry)).unwrap();
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
            "--native-video",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(fields.len(), if color { 11 } else { 9 }, "{entry}");
        assert_eq!(
            (fields[0], fields[1], fields[2], fields[3]),
            ("00", "00000043", "D000", "OK"),
            "{entry}"
        );
        let mut vram = [0xa7; 8192];
        if color {
            vram[..count.min(1024)].fill(0x3c);
            if count > 1024 {
                vram[1024..count].fill(0xc3);
            }
            assert_heap_snapshot(fields[7], &[0xb8; 8192], &entry);
            assert_heap_snapshot(fields[5], &[0x6b; 4096], &entry);
        }
        assert_heap_snapshot(fields[if color { 6 } else { 5 }], &vram, &entry);
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn memory_copy_exact_extents_alignment_and_cost(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
    #[values(false, true)] bulk: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "MemoryCopy");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let data: Vec<u8> = (0..3844).map(|i| (i ^ (i >> 8)) as u8).collect();
    for alignment in 0..4 {
        let entry = format!("Program::{}{alignment}", if bulk { "Bulk" } else { "Main" });
        let mut object = compile::object(&resolver, Some(&entry)).unwrap();
        object.sections.push(koh_objects::Section {
            name: ".test.copy.source".into(),
            kind: 0,
            address: Some(0x2000),
            size: data.len() as u32,
            data: data.clone(),
            ..Default::default()
        });
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
            "--native-heap-cost",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(fields.len(), if color { 7 } else { 6 });
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        let source = (alignment + 1) & 3;
        let mut expected = [0x5a; 4096];
        if bulk {
            expected[0x80 + alignment..0x80 + alignment + 3840]
                .copy_from_slice(&data[source..source + 3840]);
        } else {
            let mut destination = 0x100 + alignment;
            for count in [0, 1, 2, 3, 4, 5, 7, 15, 16, 17, 255, 256, 257, 300] {
                expected[destination..destination + count]
                    .copy_from_slice(&data[source..source + count]);
                destination += count + 16;
            }
            expected[4092..].copy_from_slice(&data[source..source + 4]);
        }
        assert_heap_snapshot(fields[4], &expected, &entry);
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], &entry);
        }
        let cycles: u64 = fields.last().unwrap().parse().unwrap();
        println!("{configuration} color={color} bulk={bulk} alignment={alignment}: {cycles}T");
        assert!(
            cycles < 350000,
            "{entry}: {cycles}T exceeds five-frame copy budget"
        );
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn memory_copy_contract_aliases_and_bus_wrap(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "MemoryCopy");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for shape in ["Address", "Return", "Destination", "Source", "Count"] {
        let error = compile::object(&resolver, Some(&format!("Program::Bad{shape}"))).unwrap_err();
        assert!(
            error.contains("Invalid Koh memcopy intrinsic contract") && error.contains("IL_"),
            "{shape}: {error}"
        );
    }
    for entry in ["BadBank", "IndirectBadBank"] {
        let error = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap_err();
        assert!(
            error.contains("WRAM bank writes reachable from a pinned method")
                && error.contains("IL_"),
            "{error}"
        );
    }
    let link = |entry: &str| {
        let mut object = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap();
        object.sections.push(koh_objects::Section {
            name: ".test.copy.source".into(),
            kind: 0,
            address: Some(0x2000),
            size: 4,
            data: vec![0x33, 0x44, 0x55, 0x66],
            ..Default::default()
        });
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        koh_link::link(
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
        .unwrap()
    };
    for entry in ["WrapDestination", "WrapSource", "ZeroBus"] {
        let linked = link(entry);
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-copy-wrap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(fields.len(), if color { 8 } else { 7 }, "{entry}");
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        let mut heap = [0x5a; 4096];
        let (reads, writes) = match entry {
            "WrapDestination" => ("-".into(), "FFFE:33,FFFF:44,0000:55,0001:66"),
            "WrapSource" => {
                heap[0x100..0x104].copy_from_slice(&[0x11, 4, linked.rom[0], linked.rom[1]]);
                (
                    format!(
                        "FFFE:11,FFFF:04,0000:{:02X},0001:{:02X}",
                        linked.rom[0], linked.rom[1]
                    ),
                    "-",
                )
            }
            _ => ("-".into(), "-"),
        };
        assert_heap_snapshot(fields[4], &heap, entry);
        assert_eq!(
            &fields[fields.len() - 2..],
            &[reads.as_str(), writes],
            "{entry}"
        );
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
    for (entry, result) in [
        ("ForwardOverlap", "78787878"),
        ("BackwardOverlap", "12123456"),
        ("SameAddress", "12345678"),
        ("LocalNoOp", "12345678"),
        ("PinnedCopy", "0000002A"),
        ("OverlapSeven", "0000002A"),
        ("CopyFillIntegration", "0000002A"),
        ("BankTwoCopy", "0000002A"),
    ] {
        let linked = link(entry);
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", result, "D000", "OK"], "{entry}");
        let mut heap = [0x5a; 4096];
        if entry == "PinnedCopy" {
            heap[..4].copy_from_slice(&[2, 0, 42, 17]);
            heap[0x100..0x102].copy_from_slice(&[42, 17]);
        } else if entry == "OverlapSeven" {
            heap[0x100..0x10a].copy_from_slice(&[3, 4, 5, 6, 7, 8, 9, 7, 8, 9]);
        } else if entry == "CopyFillIntegration" {
            heap[4092..].fill(1);
        } else if entry == "BankTwoCopy" && !color {
            heap[0x100..0x104].copy_from_slice(&[0x33, 0x44, 0x55, 0x66]);
        }
        assert_heap_snapshot(fields[4], &heap, entry);
        if color {
            let mut bank2 = [0x6b; 4096];
            if entry == "BankTwoCopy" {
                bank2[0x100..0x104].copy_from_slice(&[0x33, 0x44, 0x55, 0x66]);
            }
            assert_heap_snapshot(fields[5], &bank2, entry);
        }
    }
    let mut costs = Vec::new();
    for count in [64, 256, 1920] {
        let linked = link(&format!("Cost{count}"));
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap-cost",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"]);
        assert_heap_snapshot(fields[4], &[0x5a; 4096], "copy cost storage");
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], "copy cost bank2");
        }
        costs.push(fields.last().unwrap().parse::<u64>().unwrap());
    }
    assert!(costs[0] <= 64000, "64-byte copy: {}T", costs[0]);
    let marginal = (costs[2] - costs[1]) as f64 / (1920 - 256) as f64;
    println!("{configuration} color={color} copy costs={costs:?}, marginal={marginal}T/byte");
    assert!((0.0..=360.0).contains(&marginal));
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn memory_copy_desktop_body_preserves_extents(host: &Host, #[case] configuration: &str) {
    let (directory, _) = gameboy_images(host, configuration, "MemoryCopy");
    let result = dotnet()
        .arg(
            directory
                .path()
                .join(format!("bin/{configuration}/net10.0/MemoryCopy.dll")),
        )
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}{}",
        String::from_utf8_lossy(&result.stdout),
        String::from_utf8_lossy(&result.stderr)
    );
    assert_eq!(
        String::from_utf8(result.stdout).unwrap().trim(),
        "224 copies OK"
    );
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn memory_fill_exact_extents_alignment_and_cost(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "MemoryFill");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for shape in ["Address", "Return", "Pointer", "Value", "Count"] {
        let error = compile::object(&resolver, Some(&format!("Program::Bad{shape}"))).unwrap_err();
        assert!(
            error.contains("Invalid Koh memfill intrinsic contract") && error.contains("IL_"),
            "{shape}: {error}"
        );
    }
    for alignment in 0..4 {
        let entry = format!("Program::Main{alignment}");
        let object = compile::object(&resolver, Some(&entry)).unwrap();
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
            "--native-heap-cost",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(fields.len(), if color { 7 } else { 6 });
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        let mut expected = vec![0x5a; 4096];
        let mut start = 0x100 + alignment;
        for (count, value) in [
            (0, 0xaa),
            (1, 0xbb),
            (2, 0x80),
            (3, 0x7f),
            (4, 0xcc),
            (5, 0),
            (7, 0xcc),
            (15, 0xff),
            (16, 0x19),
            (17, 0xe7),
            (255, 0x18),
            (256, 0x80),
            (257, 0xef),
            (300, 0x7e),
        ] {
            expected[start..start + count].fill(value);
            start += count + 16;
        }
        expected[4092..].fill(0x7f);
        assert_heap_snapshot(fields[4], &expected, &entry);
        if color {
            assert_eq!(fields[5], "6B".repeat(4096), "{entry} changed WRAM bank2");
        }
        let cycles: u64 = fields.last().unwrap().parse().unwrap();
        println!("{configuration} color={color} alignment={alignment}: {cycles}T");
        assert!(
            cycles < 350000,
            "{entry}: {cycles}T exceeds five-frame fill budget"
        );
    }
    let object = compile::object(&resolver, Some("Program::Wrap")).unwrap();
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
        "--native-fill-wrap",
    );
    let fields: Vec<_> = text.split_whitespace().collect();
    assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"]);
    assert_eq!(fields.last().unwrap(), &"FFFE:A7,FFFF:A7,0000:A7,0001:A7");
    assert_heap_snapshot(fields[4], &[0x5a; 4096], "wrapped fill");
    if color {
        assert_heap_snapshot(fields[5], &[0x6b; 4096], "wrapped fill bank2");
    }
    for (entry, result) in [("LocalAlias", "12A7A7A7"), ("LocalNoOp", "12345678")] {
        let object = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap();
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
        assert_eq!(&fields[..4], &["00", result, "D000", "OK"], "{entry}");
        assert_heap_snapshot(fields[4], &[0x5a; 4096], entry);
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn memory_fill_desktop_body_preserves_extents(host: &Host, #[case] configuration: &str) {
    let (directory, _) = gameboy_images(host, configuration, "MemoryFill");
    let output = dotnet()
        .arg(
            directory
                .path()
                .join(format!("bin/{configuration}/net10.0/MemoryFill.dll")),
        )
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8(output.stdout).unwrap().trim(),
        "56 fills OK"
    );
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn memory_fill_respects_pinned_bank_contract(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "MemoryFill");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for entry in ["BadBank", "IndirectBadBank"] {
        let result = compile::object(&resolver, Some(&format!("Program::{entry}")));
        assert!(
            result.is_err(),
            "{entry}: accepted a fill spanning SVBK while pinned"
        );
        let error = result.err().unwrap();
        assert!(
            error.contains("IL_")
                && error.contains("WRAM bank writes reachable from a pinned method"),
            "{error}"
        );
    }
    for entry in ["ZeroBank", "PinnedFill"] {
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
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        let mut expected = vec![0x5a; 4096];
        expected[..3].copy_from_slice(&[1, 0, 42]);
        if entry == "PinnedFill" {
            expected[0x100..0x103].fill(2);
        }
        assert_heap_snapshot(fields[4], &expected, entry);
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn cpu_control_and_speed_switch_execute(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "CpuControl");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, kind) in [
        ("BadReturn", "di"),
        ("BadParameter", "nop"),
        ("BadAddress", "stop"),
    ] {
        let error = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap_err();
        assert!(
            error.contains(&format!("Invalid Koh {kind} intrinsic contract"))
                && error.contains("IL_"),
            "{error}"
        );
    }
    for entry in ["Program::Raw", "Program::Main", "Program::Wait"] {
        let object = compile::object(&resolver, Some(entry)).unwrap();
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
            "--native-cpu",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(fields.len(), 6);
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        assert_eq!(
            fields[4],
            if color && entry == "Program::Main" {
                "0:1:0:0:FE"
            } else if color {
                "0:0:0:0:7E"
            } else {
                "0:0:0:0:FF"
            },
            "{entry}"
        );
        assert_eq!(
            fields[5],
            match entry {
                "Program::Raw" => "1:0",
                "Program::Wait" => "0:1",
                _ => "0:0",
            }
        );
    }
}

#[rstest::fixture]
#[once]
fn showcase_host() -> Host {
    support::build_fixture_host("ShowcaseHost")
}

#[rstest]
#[case("Debug", true)]
#[case("Release", true)]
#[case("Debug", false)]
#[case("Release", false)]
fn lazy_maps_preserve_uniform_backgrounds_and_raw_storage(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "MapTransfers");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for window in [false, true] {
        let bad = compile::object(
            &resolver,
            Some(if window {
                "Program::BadWindowIndex"
            } else {
                "Program::BadBgIndex"
            }),
        )
        .unwrap();
        let bad = koh_link::link(
            &[bad],
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
        let bad = execute_mode(host, directory.path(), &bad, "__koh_abort", "--native-maps");
        assert_eq!(
            bad.split_whitespace().next(),
            Some("06"),
            "invalid cell must not become a no-op"
        );
        let object = compile::object(
            &resolver,
            Some(if window {
                "Program::LazyWindow"
            } else {
                "Program::LazyBg"
            }),
        )
        .unwrap();
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
            "--native-maps",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"]);
        let mut heap = vec![0x5a; 4096];
        heap[254..256].copy_from_slice(&[160, 0]);
        heap[256..416].fill(0);
        heap[430..432].copy_from_slice(&[0, 4]);
        heap[432..1456].fill(if window { 0 } else { 0x33 });
        heap[432 + 67] = if window { 0x11 } else { 0x44 };
        heap[1596] = 42;
        heap[4095] = 43;
        assert_heap_snapshot(fields[4], &heap, "one map shadow and raw buffer");
        let mut vram = vec![0xa7; 8192];
        vram[0x1800..0x1c00].fill(0x33);
        vram[0x1c00..].fill(0);
        vram[if window { 0x1c00 + 67 } else { 0x1800 + 67 }] = if window { 0x11 } else { 0x44 };
        assert_heap_snapshot(
            fields[if color { 6 } else { 5 }],
            &vram,
            "uniform maps and changed cell",
        );
        assert_heap_snapshot(
            fields[if color { 9 } else { 7 }],
            &[0; 160],
            "hidden sprites",
        );
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], "untouched WRAM2");
            assert_heap_snapshot(fields[7], &[0xb8; 8192], "untouched VRAM1");
        }
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn raw_arena_shares_capacity_without_resetting_managed_arrays(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "RawArena");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for (entry, kind) in [
        ("InvalidAllocation", "alloc"),
        ("InvalidReset", "heapreset"),
    ] {
        let error = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap_err();
        assert!(
            error.contains(&format!("Invalid Koh {kind} intrinsic contract"))
                && error.contains("IL_"),
            "{error}"
        );
    }
    for (entry, succeeds) in [
        ("Main", true),
        ("ResetOnly", true),
        ("ResetWithManagedArray", true),
        ("BankPreservation", true),
        ("FullArena", true),
        ("ExactSharedCapacity", true),
        ("ManagedThenRawCollision", false),
        ("RawThenManagedCollision", false),
        ("AlignedCollision", false),
        ("NegativeSize", false),
        ("Oversize", false),
        ("TruncatedSize", false),
        ("Exhausted", false),
    ] {
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
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            if succeeds {
                "__koh_exit"
            } else {
                "__koh_abort"
            },
            "--native-arena",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(fields.len(), if color { 7 } else { 6 }, "{entry}");
        assert_eq!(fields[0], if succeeds { "00" } else { "05" }, "{entry}");
        assert_eq!(fields[3], "OK", "{entry}");
        if succeeds {
            assert_eq!(&fields[1..3], &["0000002A", "D000"], "{entry}");
        }
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], "untouched WRAM2");
        }
        if entry == "Main" {
            assert_eq!(*fields.last().unwrap(), "2A", "MMIO round trip");
            let mut expected = vec![0x5a; 4096];
            expected[4092] = 11;
            expected[4088] = 22;
            assert_heap_snapshot(fields[4], &expected, "distinct raw blocks");
        } else if entry == "FullArena" {
            let mut expected = vec![0x5a; 4096];
            expected[0] = 42;
            expected[4095] = 99;
            assert_heap_snapshot(fields[4], &expected, "full raw arena");
        } else if entry == "ExactSharedCapacity" || entry == "ManagedThenRawCollision" {
            let mut expected = vec![0x5a; 4096];
            expected[..4092].fill(0);
            expected[..2].copy_from_slice(&[0xfa, 0x0f]);
            expected[4091] = 99;
            if succeeds {
                expected[4092] = 42;
                expected[4095] = 43;
            }
            assert_heap_snapshot(fields[4], &expected, "managed array and raw boundary");
        } else if entry == "AlignedCollision" {
            let mut expected = vec![0x5a; 4096];
            expected[254..257].copy_from_slice(&[1, 0, 0]);
            assert_heap_snapshot(fields[4], &expected, "aligned array remains intact");
        } else if entry == "ResetWithManagedArray" {
            let mut expected = vec![0x5a; 4096];
            expected[..5].copy_from_slice(&[3, 0, 11, 0, 22]);
            expected[4092] = 42;
            assert_heap_snapshot(fields[4], &expected, "reset preserves live managed storage");
        } else if entry == "BankPreservation" {
            let mut expected = vec![0x5a; 4096];
            expected[4092] = 43;
            assert_heap_snapshot(fields[4], &expected, "raw bank1 storage");
        } else if !succeeds || entry == "ResetOnly" {
            assert_heap_snapshot(
                fields[4],
                &[0x5a; 4096],
                "allocation must not write raw payload",
            );
        }
    }
}

#[rstest]
fn raw_arena_desktop_boundary_is_checked(host: &Host) {
    let (directory, _) = gameboy_images(host, "Release", "RawArenaDesktop");
    let result = dotnet()
        .arg(
            directory
                .path()
                .join("bin/Release/net10.0/RawArenaDesktop.dll"),
        )
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert_eq!(
        String::from_utf8(result.stdout).unwrap().trim(),
        "8188 33 True 1:1:1:2 True 42:99"
    );
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn full_graphics_showcase_boots_and_animates(
    showcase_host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let directory = tempfile::tempdir_in(showcase_host.directory.path()).unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let fixture =
        Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Showcase/Showcase.csproj");
    let project = directory.path().join("Showcase.csproj");
    std::fs::copy(fixture, &project).unwrap();
    let built = dotnet()
        .arg("build")
        .arg(project)
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .arg(format!("-p:KohRoot={}", root.display()))
        .output()
        .unwrap();
    assert!(
        built.status.success(),
        "{}{}",
        String::from_utf8_lossy(&built.stdout),
        String::from_utf8_lossy(&built.stderr)
    );
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let object_path = directory.path().join("showcase.o");
    let compiled = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
        .arg(output.join("Showcase.dll"))
        .arg("-r")
        .arg(output.join("Koh.GameBoy.dll"))
        .arg("-o")
        .arg(&object_path)
        .output()
        .unwrap();
    assert!(
        compiled.status.success(),
        "{}",
        String::from_utf8_lossy(&compiled.stderr)
    );
    let object = koh_objects::read_elf(&std::fs::read(object_path).unwrap()).unwrap();
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
    assert!(linked.rom.len() > 0x8000, "exercise an actual banked game");
    let rom = directory.path().join("showcase.gb");
    std::fs::write(&rom, linked.rom).unwrap();
    let result = dotnet().arg(&showcase_host.dll).arg(&rom).output().unwrap();
    assert!(
        result.status.success(),
        "{}{}",
        String::from_utf8_lossy(&result.stdout),
        String::from_utf8_lossy(&result.stderr)
    );
    let report: Vec<u32> = String::from_utf8(result.stdout)
        .unwrap()
        .split_whitespace()
        .map(|s| s.parse().unwrap())
        .collect();
    assert_eq!(report.len(), 5);
    assert!(report[0] > 0 && report[0] < 600, "real boot handoff");
    assert!(report[1] < 2000, "bounded startup");
    assert!(
        report[2] > 0 && report[3] > 0,
        "scroll and sprites must animate"
    );
    if report[4] != 0 {
        let images = ["Showcase.dll", "Koh.GameBoy.dll"]
            .map(|name| std::fs::read(output.join(name)).unwrap());
        let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
        for line in String::from_utf8_lossy(&result.stderr)
            .lines()
            .filter(|line| line.starts_with("mode3 "))
        {
            let fields: Vec<_> = line.split_whitespace().collect();
            let bank = fields[1].parse().unwrap();
            let pc = i32::from_str_radix(fields[2], 16).unwrap();
            if let Some(owner) = linked
                .symbols
                .iter()
                .filter(|s| {
                    s.bank == Some(bank)
                        && s.value <= pc
                        && s.name.starts_with("__koh_a")
                        && !s.name.contains('.')
                })
                .max_by_key(|s| s.value)
            {
                let (assembly, token) = owner
                    .name
                    .strip_prefix("__koh_a")
                    .unwrap()
                    .split_once("_m")
                    .unwrap();
                let name = resolver
                    .assembly(assembly.parse().unwrap())
                    .unwrap()
                    .method_name(u32::from_str_radix(token, 16).unwrap())
                    .unwrap_or_else(|_| owner.name.clone());
                eprintln!("{line}: {name}+{}", pc - owner.value);
            }
        }
    }
    assert_eq!(
        report[4],
        0,
        "no mode-3 VRAM writes: {}",
        String::from_utf8_lossy(&result.stderr)
    );
    let snapshot = std::fs::read(rom.with_extension("gb.snapshot")).unwrap();
    assert_eq!(snapshot.len(), 32 + 1024 + 1024 + 160);
    for (i, byte) in snapshot[..32].iter().enumerate() {
        assert_eq!(*byte, if (i < 16) == (i % 2 == 0) { 255 } else { 0 });
    }
    for row in 0..32 {
        for col in 0..32 {
            assert_eq!(snapshot[32 + row * 32 + col], ((row + col) & 1) as u8);
        }
    }
    assert_eq!(
        &snapshot[1056..1062],
        &[67, 51, 63, 66, 53, 16],
        "SCORE window text"
    );
    let oam = &snapshot[2080..];
    for i in 0..4 {
        assert_eq!(&oam[i * 4 + 2..i * 4 + 4], &[2, (i * 32) as u8]);
        assert!((64..=112).contains(&oam[i * 4]));
        assert!((64..=112).contains(&oam[i * 4 + 1]));
    }
    let mut dx = i16::from(oam[1]) - 88;
    let mut dy = i16::from(oam[0]) - 88;
    assert!([(24, 0), (22, 9), (17, 17), (9, 22), (0, 24)].contains(&(dx.abs(), dy.abs())));
    for i in 1..4 {
        (dx, dy) = (-dy, dx);
        assert_eq!(
            (i16::from(oam[i * 4 + 1]) - 88, i16::from(oam[i * 4]) - 88),
            (dx, dy),
            "quarter-turn sprite spacing"
        );
    }
    assert!(
        oam[16..].iter().all(|b| *b == 0),
        "unused sprites remain hidden"
    );
    let hud = std::fs::read(rom.with_extension("gb.hud")).unwrap();
    assert_eq!(hud.len(), 120 * 5);
    let mut scores: Vec<u32> = hud
        .chunks_exact(5)
        .filter_map(|digits| {
            let first = digits.iter().position(|digit| *digit != 16)?;
            digits[first..].iter().try_fold(0, |value, digit| {
                (32..=41)
                    .contains(digit)
                    .then(|| value * 10 + u32::from(*digit) - 32)
            })
        })
        .filter(|value| *value > 0 && value % 17 == 0)
        .collect();
    scores.dedup();
    assert!(
        scores
            .windows(2)
            .any(|pair| pair[0].checked_add(17) == Some(pair[1])),
        "completed HUD scores must advance by17: {scores:?}; raw={:?}; report={report:?}",
        hud.chunks_exact(5)
            .collect::<std::collections::BTreeSet<_>>()
    );
    let rgba = std::fs::read(rom.with_extension("gb.rgba")).unwrap();
    assert_eq!(rgba.len(), 160 * 144 * 4);
    let palettes: Vec<[[u8; 4]; 2]> = if color {
        vec![
            [[165, 206, 165, 255], [66, 115, 66, 255]],
            [[0, 165, 206, 255], [0, 66, 115, 255]],
        ]
    } else {
        vec![
            [[170, 170, 170, 255], [85, 85, 85, 255]],
            [[255, 255, 255, 255], [170, 170, 170, 255]],
        ]
    };
    assert!(
        palettes.iter().any(|colors| (0..16).any(|phase| {
            (0..160).all(|x| rgba[x * 4..x * 4 + 4] == colors[((x + phase) / 8) & 1])
        })),
        "rendered checkerboard must match a documented palette and scroll phase"
    );
    if configuration == "Release" && color {
        let mut corrupt = std::fs::read(&rom).unwrap();
        corrupt[0x104] = 0;
        let bad = directory.path().join("bad-logo.gb");
        std::fs::write(&bad, corrupt).unwrap();
        let rejected = dotnet().arg(&showcase_host.dll).arg(bad).output().unwrap();
        assert!(!rejected.status.success());
        assert!(String::from_utf8_lossy(&rejected.stderr).contains("Boot did not hand off"));
    }
}

fn build(directory: &Path, configuration: &str, output: &str, ambiguous: bool) -> PathBuf {
    build_with(
        directory,
        configuration,
        output,
        if ambiguous { "AMBIGUOUS" } else { "" },
    )
}

fn build_with(directory: &Path, configuration: &str, output: &str, definitions: &str) -> PathBuf {
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Cli");
    for name in ["Cli.csproj", "Program.cs"] {
        std::fs::copy(fixture.join(name), directory.join(name)).unwrap();
    }
    let result = dotnet()
        .arg("build")
        .arg(directory.join("Cli.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .arg(format!("-p:OutputType={output}"))
        .arg(format!("-p:DefineConstants={definitions}"))
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&result.stdout),
        String::from_utf8_lossy(&result.stderr)
    );
    directory.join(format!("bin/{configuration}/net10.0/Cli.dll"))
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn roslyn_switch_tables_execute(host: &Host, #[case] configuration: &str) {
    let directory = tempfile::Builder::new()
        .prefix("koh switch tables ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let path = build(directory.path(), configuration, "Library", false);
    let bytes = std::fs::read(&path).unwrap();
    let resolver = Resolver::new(&[&bytes]).unwrap();
    let assembly = resolver.assembly(0).unwrap();
    let token = (1..=assembly.metadata().method_defs.len())
        .map(|i| 0x06000000 | i as u32)
        .find(|&token| assembly.method_name(token).unwrap() == "Program::Select")
        .unwrap();
    let body = assembly.method_body(token).unwrap();
    assert!(
        koh_compile::cil::decode(body.il)
            .unwrap()
            .iter()
            .any(|i| i.opcode == 0x45),
        "Roslyn {configuration} must exercise an actual switch table"
    );
    let output = directory.path().join("switch.o");
    let result = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
        .arg(&path)
        .args(["--entry", "Program::Switches", "-o"])
        .arg(&output)
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    let object = koh_objects::read_elf(&std::fs::read(output).unwrap()).unwrap();
    let linked = koh_link::link(&[object], &koh_link::LinkOptions::default()).unwrap();
    let mut expected = 0u32;
    for value in [42i32, 17, -23, 91, 91, -1000, 65536, 12345, -6789, 42, 42] {
        expected = expected.wrapping_mul(31).wrapping_add(value as u32);
    }
    expected ^= 42;
    assert_eq!(
        execute(host, directory.path(), &linked, "__koh_exit"),
        format!("00 {expected:08X} D000 OK")
    );
}

#[rstest]
#[case("Debug", false, 1)]
#[case("Release", false, 257)]
#[case("Debug", true, 257)]
#[case("Release", true, 1)]
fn managed_calls_and_recursion_restore_rom_banks(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
    #[case] first_bank: i32,
) {
    let directory = tempfile::tempdir_in(host.directory.path()).unwrap();
    let path = build(directory.path(), configuration, "Exe", false);
    let assembly = std::fs::read(path).unwrap();
    let resolver = Resolver::new(&[&assembly]).unwrap();
    for entry in [None, Some("BankedAbi::Run")] {
        let mut object = compile::object(&resolver, entry).unwrap();
        let mut bank = first_bank;
        for section in &mut object.sections {
            if section.name.starts_with(".text.__koh_a") {
                assert_eq!(section.kind, 1, "managed code must use ROMX");
                section.bank = Some(bank);
                bank += 1;
            }
        }
        assert!(bank >= first_bank + 2, "fixture must call another function");
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
        assert_eq!(linked.rom[0x147], 0x19, "MBC5 cartridge contract");
        assert_eq!(
            execute(host, directory.path(), &linked, "__koh_exit"),
            "00 0000002A D000 OK"
        );
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn pinned_array_pointers_copy_tiles_and_restore_banks(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let directory = tempfile::Builder::new()
        .prefix("koh pinned graphics ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let source = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Pinned");
    for name in ["Pinned.csproj", "Program.cs"] {
        std::fs::copy(source.join(name), directory.path().join(name)).unwrap();
    }
    let result = dotnet()
        .arg("build")
        .arg(directory.path().join("Pinned.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .arg(format!("-p:KohRoot={}", root.display()))
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}{}",
        String::from_utf8_lossy(&result.stdout),
        String::from_utf8_lossy(&result.stderr)
    );
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let images =
        ["Pinned.dll", "Koh.GameBoy.dll"].map(|name| std::fs::read(output.join(name)).unwrap());
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let assembly = resolver.assembly(0).unwrap();
    let mut shapes = std::collections::BTreeSet::new();
    for token in (1..=assembly.metadata().method_defs.len()).map(|i| 0x06000000 | i as u32) {
        for ty in assembly.local_types(token).unwrap() {
            if let clrmeta::TypeSig::Pinned(inner) = ty {
                shapes.insert(match *inner {
                    clrmeta::TypeSig::ByRef(_) => "byref",
                    clrmeta::TypeSig::SzArray(_) => "array",
                    _ => "other",
                });
            }
        }
    }
    assert_eq!(shapes, std::collections::BTreeSet::from(["array", "byref"]));
    for (name, expected) in [
        ("Copy", 42),
        ("Empty", 42),
        ("Null", 42),
        ("Nested", 42),
        ("Recursive", 25),
        ("Loop", 5),
        ("Unbanked", 42),
    ] {
        let entry = format!("Program::{}{name}", if color { "Banked" } else { "" });
        let object = compile::object(&resolver, Some(&entry)).unwrap();
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                cgb: if color {
                    koh_link::CgbMode::Only
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
        assert_eq!(fields.len(), if color { 6 } else { 5 }, "{name}");
        assert_eq!(
            (fields[0], fields[1], fields[2], fields[3]),
            ("00", format!("{expected:08X}").as_str(), "D000", "OK"),
            "{name}"
        );
        let mut heap = vec![0x5a; 4096];
        let bytes: &[u8] = match name {
            "Copy" => &[8, 0, 0x12, 0x34, 0x56, 0x78, 0x9a, 0xa5, 0xde, 0xf0],
            "Empty" => &[0, 0],
            "Nested" => &[2, 0, 0x34, 22, 1, 0, 0xcd, 0xab],
            "Recursive" => &[1, 0, 7, 1, 0, 6, 1, 0, 5, 1, 0, 4, 1, 0, 3],
            "Loop" => &[1, 0, 5],
            _ => &[],
        };
        heap[..bytes.len()].copy_from_slice(bytes);
        assert_heap_snapshot(fields[4], &heap, name);
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], name);
        }
    }
    for name in ["BadBank", "IndirectBadBank"] {
        let error = compile::object(&resolver, Some(&format!("Program::{name}"))).unwrap_err();
        assert!(
            error.contains("IL_")
                && error.contains("WRAM bank writes reachable from a pinned method"),
            "{name}: {error}"
        );
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn actual_tileset_load_copies_vram_and_preserves_banks(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let directory = tempfile::Builder::new()
        .prefix("koh real tileset ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let source = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Tiles");
    for name in ["Tiles.csproj", "Program.cs"] {
        std::fs::copy(source.join(name), directory.path().join(name)).unwrap();
    }
    let result = dotnet()
        .arg("build")
        .arg(directory.path().join("Tiles.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .arg(format!("-p:KohRoot={}", root.display()))
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}{}",
        String::from_utf8_lossy(&result.stdout),
        String::from_utf8_lossy(&result.stderr)
    );
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let images =
        ["Tiles.dll", "Koh.GameBoy.dll"].map(|name| std::fs::read(output.join(name)).unwrap());
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let data = [
        0x3c, 0x7e, 0x42, 0x42, 0x42, 0x42, 0x42, 0x42, 0x7e, 0x5e, 0x7e, 0x0a, 0x7c, 0x56, 0x38,
        0x7c, 0xff, 0x00, 0x7e, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81,
        0x7e, 0xff,
    ];
    for (name, aligned, render) in [
        ("Unaligned", false, false),
        ("Aligned", true, false),
        ("OnAligned", true, false),
        ("OnLargeAligned", true, false),
        ("OnUnaligned", false, false),
        ("VideoInit", false, false),
        ("Render", false, true),
    ] {
        let initialize = name == "VideoInit";
        let lcd_on = name.starts_with("On");
        let large = name == "OnLargeAligned";
        if large && !color {
            continue;
        }
        let entry = format!("Program::{}{}", if color { "Banked" } else { "" }, name);
        let object = compile::object(&resolver, Some(&entry)).unwrap();
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                cgb: if color {
                    koh_link::CgbMode::Only
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
            if render {
                "--native-frame"
            } else {
                "--native-video"
            },
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(fields.len(), if color { 11 } else { 9 }, "{entry}");
        assert_eq!(
            (fields[0], fields[1], fields[2], fields[3]),
            (
                "00",
                if color { "00000043" } else { "00000044" },
                "D000",
                "OK"
            ),
            "{entry}"
        );
        let mut heap = vec![0x5a; 4096];
        let offset = if aligned {
            heap[..14].copy_from_slice(&[12, 0, 42, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            14
        } else {
            0
        };
        let pattern = [
            0xaa, 0xcc, 0x55, 0x33, 0xaa, 0xcc, 0x55, 0x33, 0xaa, 0xcc, 0x55, 0x33, 0xaa, 0xcc,
            0x55, 0x33,
        ];
        let large_data: Vec<u8> = (0..1920).map(|i| i as u8).collect();
        let lcd_data: Vec<u8> = (1..=32).collect();
        let payload: &[u8] = if large {
            &large_data
        } else if lcd_on {
            &lcd_data
        } else if render {
            &pattern
        } else {
            &data
        };
        if initialize {
            heap[254..256].copy_from_slice(&[160, 0]);
            heap[256..416].fill(0);
        } else {
            heap[offset..offset + 2].copy_from_slice(&(payload.len() as u16).to_le_bytes());
            heap[offset + 2..offset + 2 + payload.len()].copy_from_slice(payload);
        }
        assert_heap_snapshot(fields[4], &heap, &entry);
        let mut vram = vec![0xa7; 8192];
        if initialize {
            vram[0x1800..0x2000].fill(0);
        } else if render {
            vram[..16].copy_from_slice(&pattern);
            vram[0x1800..0x1c00].fill(0);
        } else {
            let offset = if lcd_on && !large { 112 } else { 48 };
            vram[offset..offset + payload.len()].copy_from_slice(payload);
        }
        assert_heap_snapshot(fields[if color { 6 } else { 5 }], &vram, &entry);
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], &entry);
            let mut attributes = [0xb8; 8192];
            if render {
                attributes[0x1800..0x1c00].fill(0);
            }
            assert_heap_snapshot(fields[7], &attributes, &entry);
        }
        if render {
            let pixels = std::fs::read(directory.path().join("native game.rgba")).unwrap();
            assert_eq!(pixels.len(), 160 * 144 * 4);
            for y in 0..144 {
                for x in 0..160 {
                    let bit = 7 - (x & 7);
                    let lo = if y & 1 == 0 { 0xaau8 } else { 0x55 };
                    let hi = if y & 1 == 0 { 0xccu8 } else { 0x33 };
                    let index = ((lo >> bit) & 1) | (((hi >> bit) & 1) << 1);
                    let expected = if color {
                        [
                            [255, 255, 255, 255],
                            [255, 0, 0, 255],
                            [0, 255, 0, 255],
                            [0, 0, 255, 255],
                        ][index as usize]
                    } else {
                        let shade = [255, 170, 85, 0][index as usize];
                        [shade, shade, shade, 255]
                    };
                    let offset = (y * 160 + x) * 4;
                    assert_eq!(
                        &pixels[offset..offset + 4],
                        &expected,
                        "{entry}: pixel {x},{y}"
                    );
                }
            }
            let png = std::fs::read(directory.path().join("native game.png")).unwrap();
            assert!(png.starts_with(&[137, 80, 78, 71, 13, 10, 26, 10]));
            assert_eq!(&png[16..24], &[0, 0, 0, 160, 0, 0, 0, 144]);
        }
        let (before, after) = fields[if color { 8 } else { 6 }].split_once(':').unwrap();
        assert_eq!(after.len(), 14, "{entry}: malformed DMA state");
        assert_eq!(
            &after[8..],
            "000000",
            "{entry}: DMA must finish in general-purpose mode"
        );
        if color && aligned {
            assert_eq!(
                &after[..8],
                if large {
                    "D4108430"
                } else if lcd_on {
                    "D0108070"
                } else {
                    "D0108030"
                },
                "{entry}: programmed DMA source/destination"
            );
        } else {
            assert_eq!(
                &after[..8],
                before,
                "{entry}: CPU-copy path must not program DMA"
            );
        }
        assert_heap_snapshot(
            fields[if color { 9 } else { 7 }],
            &vec![if initialize { 0 } else { 0xc9 }; 160],
            &entry,
        );
        let registers = fields[if color { 10 } else { 8 }];
        if initialize {
            assert_eq!(registers, "11E4E4E4", "{entry}: initialized LCD/palettes");
        } else if render {
            assert!(
                registers.starts_with("91E4"),
                "{entry}: enabled LCD/palette"
            );
        } else {
            assert!(
                registers.starts_with(if name.starts_with("On") { "91" } else { "00" }),
                "{entry}: transfer LCD state"
            );
        }
    }
}

fn gameboy_images(
    host: &Host,
    configuration: &str,
    fixture: &str,
) -> (tempfile::TempDir, [Vec<u8>; 2]) {
    let directory = tempfile::Builder::new()
        .prefix("koh gameboy fixture ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let source = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests/fixtures")
        .join(fixture);
    let project = format!("{fixture}.csproj");
    for name in [project.as_str(), "Program.cs"] {
        std::fs::copy(source.join(name), directory.path().join(name)).unwrap();
    }
    let result = dotnet()
        .arg("build")
        .arg(directory.path().join(project))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .arg(format!("-p:KohRoot={}", root.display()))
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}{}",
        String::from_utf8_lossy(&result.stdout),
        String::from_utf8_lossy(&result.stderr)
    );
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let images = [format!("{fixture}.dll"), "Koh.GameBoy.dll".into()]
        .map(|name| std::fs::read(output.join(name)).unwrap());
    (directory, images)
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn full_cgb_map_transfers_preserve_vram_and_complete(host: &Host, #[case] configuration: &str) {
    let (directory, images) = gameboy_images(host, configuration, "MapTransfers");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    for entry in ["Program::Fill", "Program::Clear"] {
        let object = compile::object(&resolver, Some(entry)).unwrap();
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                cgb: koh_link::CgbMode::Only,
                ..Default::default()
            },
        )
        .unwrap();
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-maps",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(fields.len(), 11, "{entry}");
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        let mut vram = vec![0xa7; 8192];
        vram[0x1800..0x1c00].fill(0x33);
        vram[0x1c00..].fill(0x11);
        assert_heap_snapshot(fields[6], &vram, entry);
        assert_heap_snapshot(fields[7], &[0xb8; 8192], "untouched VRAM1");
        assert_heap_snapshot(fields[9], &[0; 160], "untouched OAM");
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn oam_dma_runs_from_relocated_hram_and_preserves_banks(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "OamDma");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let object = compile::object(&resolver, Some("Program::Main")).unwrap();
    let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
    assert_eq!(object.sections.iter().filter(|s| s.kind == 7).count(), 1);
    assert_eq!(
        object.sections.iter().find(|s| s.kind == 7).unwrap().size,
        8
    );
    let reservation = |size| koh_objects::Object {
        sections: vec![koh_objects::Section {
            name: ".test.hram".into(),
            kind: 7,
            address: Some(0xff80),
            size,
            ..Default::default()
        }],
        ..Default::default()
    };
    for reserved in [0, 16, 119] {
        let mut inputs = vec![object.clone()];
        if reserved != 0 {
            inputs.push(reservation(reserved));
        }
        let linked = koh_link::link(
            &inputs,
            &koh_link::LinkOptions {
                cgb: if color {
                    koh_link::CgbMode::Only
                } else {
                    koh_link::CgbMode::Preserve
                },
                ..Default::default()
            },
        )
        .unwrap();
        let hram = linked
            .symbols
            .iter()
            .find(|s| s.name == "__koh_oamdma")
            .unwrap()
            .value;
        assert!(hram >= 0xff80 + reserved as i32);
        assert!(hram + 8 <= 0xffff);
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-oam",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(&fields[..4], ["00", "0000002A", "D000", "OK"]);
        let pattern: Vec<u8> = (0..160).map(|i| ((i * 3 + 1) as u8) ^ 255).collect();
        let mut heap = vec![0x5a; 4096];
        heap[254..256].copy_from_slice(&160u16.to_le_bytes());
        heap[256..416].copy_from_slice(&pattern);
        assert_heap_snapshot(fields[4], &heap, "DMA source");
        assert_heap_snapshot(fields[if color { 9 } else { 7 }], &pattern, "hardware OAM");
        assert_heap_snapshot(
            fields[if color { 6 } else { 5 }],
            &[0xa7; 8192],
            "untouched VRAM0",
        );
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], "untouched WRAM2");
            assert_heap_snapshot(fields[7], &[0xb8; 8192], "untouched VRAM1");
        }
        let monitor: Vec<_> = fields.last().unwrap().split(':').collect();
        assert_eq!(monitor[0], "2");
        assert!(monitor[1].parse::<usize>().unwrap() > 0);
        assert_eq!(monitor[2], "D1");
        if configuration == "Debug" && !color && reserved == 0 {
            let mut rom = linked.rom.clone();
            let source = linked
                .symbols
                .iter()
                .find(|s| s.name == "__koh_oamdma_bytes")
                .unwrap()
                .value as usize;
            assert_eq!(rom[source + 3], 40);
            rom[source + 3] = 1;
            let path = directory.path().join("short DMA.gb");
            std::fs::write(&path, rom).unwrap();
            let stop = linked
                .symbols
                .iter()
                .find(|s| s.name == "__koh_exit")
                .unwrap()
                .value;
            let output = dotnet()
                .arg(&host.dll)
                .args(["--native-oam", &format!("{stop:x}")])
                .arg(path)
                .output()
                .unwrap();
            assert!(!output.status.success());
            assert!(
                String::from_utf8_lossy(&output.stderr).contains("DMA execution outside HRAM"),
                "{}",
                String::from_utf8_lossy(&output.stderr)
            );
        }
    }
    let raw = compile::object(&resolver, Some("Program::Raw")).unwrap();
    assert!(raw.sections.iter().all(|s| s.kind != 7));
    let raw = koh_objects::read_elf(&koh_objects::write_elf(&raw).unwrap()).unwrap();
    let raw = koh_link::link(
        &[raw],
        &koh_link::LinkOptions {
            cgb: if color {
                koh_link::CgbMode::Only
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
        &raw,
        "__koh_exit",
        "--native-oam-trigger",
    );
    let fields: Vec<_> = text.split_whitespace().collect();
    assert_eq!(fields[3], "OK");
    let pattern: Vec<u8> = (0..160).map(|i| (i * 3 + 1) as u8).collect();
    let mut heap = vec![0x5a; 4096];
    heap[254..256].copy_from_slice(&160u16.to_le_bytes());
    heap[256..416].copy_from_slice(&pattern);
    assert_heap_snapshot(fields[4], &heap, "raw DMA source");
    assert_heap_snapshot(fields[if color { 9 } else { 7 }], &pattern, "raw DMA OAM");
    assert_eq!(*fields.last().unwrap(), "1:0:D1");
    if color {
        assert_heap_snapshot(fields[5], &[0x6b; 4096], "raw DMA WRAM2");
    }
    assert!(
        koh_link::link(&[object, reservation(127)], &Default::default())
            .unwrap_err()
            .contains("Cannot place")
    );
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn oam_dma_metadata_contract_rejects_invalid_signatures_and_addresses(
    host: &Host,
    #[case] configuration: &str,
) {
    let (directory, images) = gameboy_images(host, configuration, "OamDma");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let assembly = resolver.assembly(0).unwrap();
    let alias = (1..=assembly.metadata().method_defs.len())
        .map(|row| 0x06000000 | row as u32)
        .find(|&token| assembly.method_name(token).unwrap() == "Program::Alias")
        .unwrap();
    let signature = assembly.method(alias).unwrap().signature;
    assert_eq!(
        assembly.metadata().blobs.get(signature).unwrap(),
        &[0, 1, 1, 5]
    );
    let heap = assembly.metadata().blobs.data();
    let positions: Vec<_> = images[0]
        .windows(heap.len())
        .enumerate()
        .filter_map(|(i, bytes)| (bytes == heap).then_some(i))
        .collect();
    assert_eq!(positions.len(), 1);
    let signature = positions[0] + signature as usize + 1;
    let needle = b"\x01\x00\x06oamdma\xff\xff\xff\xff\x00\x00";
    let attributes: Vec<_> = images[0]
        .windows(needle.len())
        .enumerate()
        .filter_map(|(i, bytes)| (bytes == needle).then_some(i))
        .collect();
    assert_eq!(attributes.len(), 1);
    for (offset, value) in [
        (signature, 5),
        (signature + 2, 8),
        (signature + 3, 7),
        (attributes[0] + 9, 0),
    ] {
        let mut image = images[0].clone();
        image[offset] = value;
        let resolver = Resolver::new(&[&image, &images[1]]).unwrap();
        let error = koh_compile::lower::assembly_method(
            &resolver,
            koh_compile::resolve::Definition {
                assembly: 0,
                token: alias,
            },
        )
        .unwrap_err();
        assert!(
            error.contains("Program::Alias")
                && error.contains("Invalid Koh oamdma intrinsic contract"),
            "{error}"
        );
    }
    let method = |name: &str| {
        (1..=assembly.metadata().method_defs.len())
            .map(|row| 0x06000000 | row as u32)
            .find(|&token| assembly.method_name(token).unwrap() == name)
            .unwrap()
    };
    let raw = assembly.method_body(method("Program::Raw")).unwrap();
    let il = koh_compile::cil::decode(raw.il).unwrap();
    let (position, call) = il
        .iter()
        .enumerate()
        .find(|(_, i)| {
            if i.opcode != 0x28 {
                return false;
            }
            let koh_compile::cil::Operand::Token(token) = i.operand else {
                return false;
            };
            let target = resolver.resolve_method(0, token).unwrap().definition;
            resolver
                .assembly(target.assembly)
                .unwrap()
                .method_name(target.token)
                .unwrap()
                .ends_with("::set_DMA")
        })
        .unwrap();
    assert_eq!(il[position - 1].opcode, 0xd2);
    let transfer = assembly.method_body(method("Program::Transfer")).unwrap();
    let target = koh_compile::cil::decode(transfer.il)
        .unwrap()
        .into_iter()
        .find(|i| i.opcode == 0x28)
        .unwrap();
    let koh_compile::cil::Operand::Token(target) = target.operand else {
        panic!("DMA member token")
    };
    let base = raw.il.as_ptr() as usize - images[0].as_ptr() as usize;
    let mut wide = images[0].clone();
    wide[base + il[position - 1].offset as usize] = 0x67;
    wide[base + call.offset as usize + 1..base + call.offset as usize + 5]
        .copy_from_slice(&target.to_le_bytes());
    let resolver = Resolver::new(&[&wide, &images[1]]).unwrap();
    let object = compile::object(&resolver, Some("Program::Raw")).unwrap();
    let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
    let linked = koh_link::link(&[object], &Default::default()).unwrap();
    let text = execute_mode(
        host,
        directory.path(),
        &linked,
        "__koh_exit",
        "--native-oam",
    );
    let fields: Vec<_> = text.split_whitespace().collect();
    assert_eq!(&fields[..4], ["00", "0000002A", "D000", "OK"]);
    let pattern: Vec<u8> = (0..160).map(|i| (i * 3 + 1) as u8).collect();
    assert_heap_snapshot(fields[7], &pattern, "signed-byte page OAM");
    let monitor: Vec<_> = fields.last().unwrap().split(':').collect();
    assert_eq!((monitor[0], monitor[2]), ("1", "D1"));
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn string_metadata_contracts_and_direct_calls(host: &Host, #[case] configuration: &str) {
    let (directory, images) = gameboy_images(host, configuration, "Strings");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let assembly = resolver.assembly(0).unwrap();
    let method = |name: &str| {
        (1..=assembly.metadata().method_defs.len())
            .map(|row| 0x06000000 | row as u32)
            .find(|&token| assembly.method_name(token).unwrap() == name)
            .unwrap()
    };
    let at = method("Program::At");
    let unsupported = koh_compile::lower::assembly_method(
        &resolver,
        koh_compile::resolve::Definition {
            assembly: 0,
            token: method("Program::Unsupported"),
        },
    )
    .unwrap_err();
    assert!(
        unsupported.contains("Unsupported System.String member Concat"),
        "{unsupported}"
    );
    let body = assembly.method_body(at).unwrap();
    let base = body.il.as_ptr() as usize - images[0].as_ptr() as usize;
    let call = koh_compile::cil::decode(body.il)
        .unwrap()
        .into_iter()
        .find(|i| i.opcode == 0x6f)
        .unwrap();
    let koh_compile::cil::Operand::Token(token) = call.operand else {
        panic!("String getter token")
    };
    let member = &assembly.metadata().member_refs[(token & 0xffffff) as usize - 1];
    assert_eq!(
        assembly.metadata().blobs.get(member.signature).unwrap(),
        &[0x20, 1, 3, 8]
    );
    let heap = assembly.metadata().blobs.data();
    let positions: Vec<_> = images[0]
        .windows(heap.len())
        .enumerate()
        .filter_map(|(i, bytes)| (bytes == heap).then_some(i))
        .collect();
    assert_eq!(positions.len(), 1);
    let signature = positions[0] + member.signature as usize + 1;
    for (byte, value) in [(0, 0), (1, 0), (2, 8), (3, 0x18)] {
        let mut image = images[0].clone();
        image[signature + byte] = value;
        let resolver = Resolver::new(&[&image, &images[1]]).unwrap();
        let error = koh_compile::lower::assembly_method(
            &resolver,
            koh_compile::resolve::Definition {
                assembly: 0,
                token: at,
            },
        )
        .unwrap_err();
        assert!(
            error.contains("Program::At") && error.contains("IL_"),
            "{error}"
        );
    }
    let runtime = assembly
        .metadata()
        .assembly_refs
        .iter()
        .find(|reference| {
            assembly.metadata().strings.get(reference.name).unwrap() == "System.Runtime"
        })
        .unwrap();
    let mut forged = images[0].clone();
    forged[positions[0] + runtime.public_key_or_token as usize + 1] ^= 1;
    let forged = Resolver::new(&[&forged, &images[1]]).unwrap();
    let forged_error = koh_compile::lower::assembly_method(
        &forged,
        koh_compile::resolve::Definition {
            assembly: 0,
            token: at,
        },
    )
    .unwrap_err();
    assert!(
        forged_error.contains("Missing assembly System.Runtime")
            && forged_error.contains("public key token")
            && forged_error.contains("Program::At")
            && forged_error.contains("IL_"),
        "{forged_error}"
    );

    let other = method("Program::Other");
    let body = assembly.method_body(other).unwrap();
    let base_other = body.il.as_ptr() as usize - images[0].as_ptr() as usize;
    let literal = koh_compile::cil::decode(body.il)
        .unwrap()
        .into_iter()
        .find(|i| i.opcode == 0x72)
        .unwrap();
    for token in [0x04000001u32, 0x70ffffff] {
        let mut image = images[0].clone();
        image[base_other + literal.offset as usize + 1..base_other + literal.offset as usize + 5]
            .copy_from_slice(&token.to_le_bytes());
        let resolver = Resolver::new(&[&image, &images[1]]).unwrap();
        let error = koh_compile::lower::assembly_method(
            &resolver,
            koh_compile::resolve::Definition {
                assembly: 0,
                token: other,
            },
        )
        .unwrap_err();
        assert!(
            error.contains("Program::Other") && error.contains("IL_"),
            "{error}"
        );
    }

    let mut direct = images[0].clone();
    direct[base + call.offset as usize] = 0x28;
    let resolver = Resolver::new(&[&direct, &images[1]]).unwrap();
    let object = compile::object(&resolver, Some("Program::Main")).unwrap();
    let linked = koh_link::link(&[object], &Default::default()).unwrap();
    assert_eq!(
        execute(host, directory.path(), &linked, "__koh_exit"),
        "00 0000002A D000 OK"
    );
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn strings_preserve_utf16_calls_identity_and_bounds(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "Strings");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let object = compile::object(&resolver, Some("Program::TextLibrary")).unwrap();
    let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
    let size: u32 = object
        .sections
        .iter()
        .filter(|s| s.kind == 0)
        .map(|s| s.size)
        .sum();
    let linked = koh_link::link(
        &[object],
        &koh_link::LinkOptions {
            cgb: if color {
                koh_link::CgbMode::Only
            } else {
                koh_link::CgbMode::Preserve
            },
            ..Default::default()
        },
    )
    .unwrap_or_else(|error| panic!("TextLibrary ROM0 total {size}: {error}"));
    let video = execute_mode(
        host,
        directory.path(),
        &linked,
        "__koh_exit",
        "--native-video",
    );
    let fields: Vec<_> = video.split_whitespace().collect();
    assert_eq!(&fields[..4], ["00", "0000002A", "D000", "OK"]);
    let mut vram = vec![0xa7; 8192];
    vram[0x1800..].fill(0);
    vram[0x1841..0x1846].copy_from_slice(&[51, 35, 47, 50, 37]);
    vram[0x1c62..0x1c64].copy_from_slice(&[40, 41]);
    assert_heap_snapshot(fields[if color { 6 } else { 5 }], &vram, "TextLibrary VRAM");
    if color {
        assert_heap_snapshot(fields[5], &[0x6b; 4096], "TextLibrary WRAM2");
        assert_heap_snapshot(fields[7], &[0xb8; 8192], "TextLibrary VRAM1");
    }
    assert_heap_snapshot(
        fields[if color { 9 } else { 7 }],
        &[0; 160],
        "TextLibrary OAM",
    );
    assert_eq!(fields[if color { 10 } else { 8 }], "11E4E4E4");
    for (entry, fault) in [
        ("Main", 0),
        ("ArrayCopies", 0),
        ("NullLength", 4),
        ("NullIndex", 4),
        ("Negative", 6),
        ("High", 6),
        ("End", 6),
        ("Empty", 6),
    ] {
        let object = compile::object(&resolver, Some(&format!("Program::{entry}")))
            .unwrap_or_else(|error| panic!("{entry}: {error}"));
        if entry == "Main" {
            let pool: Vec<_> = object
                .sections
                .iter()
                .filter(|s| s.name == ".rodata.koh.strings")
                .collect();
            assert_eq!(pool.len(), 1);
            let units: [Vec<u16>; 4] = [
                vec![],
                vec![0, 99, 97, 102, 233, 0xd83d, 0xde00, 0xd800, 120, 0xdc00],
                vec![79, 84, 72, 69, 82],
                vec![83, 67, 79, 82, 69],
            ];
            let expected: Vec<_> = units
                .iter()
                .flat_map(|units| {
                    (units.len() as u16)
                        .to_le_bytes()
                        .into_iter()
                        .chain(units.iter().flat_map(|unit| unit.to_le_bytes()))
                        .collect::<Vec<_>>()
                })
                .collect();
            assert_eq!(pool[0].data, expected);
        }
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                cgb: if color {
                    koh_link::CgbMode::Only
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
            if fault == 0 {
                "__koh_exit"
            } else {
                "__koh_abort"
            },
            "--native-heap",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(fields[0], format!("{fault:02X}"), "{entry}: {text}");
        assert_eq!(fields[3], "OK", "{entry}: {text}");
        if fault == 0 {
            assert_eq!((fields[1], fields[2]), ("0000002A", "D000"), "{text}");
        }
        let mut expected = vec![0x5a; 4096];
        if entry == "ArrayCopies" {
            expected[..14].copy_from_slice(&[5, 0, 83, 67, 79, 82, 69, 5, 0, 83, 67, 79, 82, 69]);
        }
        assert_heap_snapshot(fields[4], &expected, entry);
        if color {
            assert_heap_snapshot(fields[5], &vec![0x6b; 4096], entry);
        }
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn aggregate_arrays_copy_alias_scale_and_preserve_banks(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "StructArrays");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let assembly = resolver.assembly(0).unwrap();
    for (name, opcode) in [("Read", 0xa3), ("Write", 0xa4)] {
        let token = (1..=assembly.metadata().method_defs.len())
            .map(|row| 0x06000000 | row as u32)
            .find(|&token| assembly.method_name(token).unwrap() == format!("Program::{name}"))
            .unwrap();
        assert!(
            koh_compile::cil::decode(assembly.method_body(token).unwrap().il)
                .unwrap()
                .iter()
                .any(|i| i.opcode == opcode),
            "{name}: fixture must exercise typed array opcode"
        );
    }
    for (entry, fault) in [
        (if color { "Banked" } else { "Main" }, 0),
        (if color { "BankedAligned" } else { "Aligned" }, 0),
        (if color { "BankedPinned" } else { "Pinned" }, 0),
        ("Capacity", 0),
        ("HugeEmpty", 0),
        ("NestedValues", 0),
        ("Null", 4),
        ("Bounds", 6),
        ("Negative", 5),
        ("OverOdd", 5),
        ("Wrap", 5),
        ("HugeTooBig", 5),
    ] {
        let object = compile::object(&resolver, Some(&format!("Program::{entry}")))
            .unwrap_or_else(|error| panic!("{entry}: {error}"));
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                cgb: if color {
                    koh_link::CgbMode::Only
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
            if fault == 0 {
                "__koh_exit"
            } else {
                "__koh_abort"
            },
            "--native-heap",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(fields.len(), if color { 6 } else { 5 }, "{entry}");
        assert_eq!(fields[0], format!("{fault:02X}"), "{entry}");
        assert_eq!(fields[3], "OK", "{entry}");
        if fault == 0 {
            assert_eq!((fields[1], fields[2]), ("0000002A", "D000"), "{entry}");
        }
        let mut heap = vec![0x5a; 4096];
        match entry {
            "Main" | "Banked" => {
                heap[..157].fill(0);
                heap[..11].copy_from_slice(&[3, 0, 4, 2, 3, 1, 9, 3, 7, 0, 0]);
                heap[15] = 2;
                heap[22..27].copy_from_slice(&[0x78, 0x56, 0x34, 0x12, 0x9a]);
                heap[27] = 2;
                heap[29] = 0xcd;
                heap[37..45].copy_from_slice(&0x0123456789abcdefu64.to_le_bytes());
                heap[93] = 0xab;
                heap[101..109].copy_from_slice(&0x0123456789abcdefu64.to_le_bytes());
            }
            "Aligned" | "BankedAligned" => {
                heap[14..22].fill(0);
                heap[14] = 2;
                heap[21] = 0xcd;
            }
            "Pinned" | "BankedPinned" => heap[..5].copy_from_slice(&[1, 0, 9, 7, 6]),
            "Capacity" => {
                heap.fill(0);
                heap[..2].copy_from_slice(&1364u16.to_le_bytes());
                heap[4093] = 0xe1;
            }
            "HugeEmpty" => heap[..2].fill(0),
            "NestedValues" => heap[..8].copy_from_slice(&[3, 0, 5, 0, 6, 0, 7, 0]),
            "Bounds" => {
                heap[..5].fill(0);
                heap[0] = 1;
            }
            _ => {}
        }
        assert_heap_snapshot(fields[4], &heap, entry);
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn actual_sprite_handles_mutate_shadow_storage(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "Structs");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let object = compile::object(
        &resolver,
        Some(if color {
            "Program::Banked"
        } else {
            "Program::Main"
        }),
    )
    .unwrap();
    let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
    let linked = koh_link::link(
        &[object],
        &koh_link::LinkOptions {
            cgb: if color {
                koh_link::CgbMode::Only
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
        "--native-video",
    );
    let fields: Vec<_> = text.split_whitespace().collect();
    assert_eq!(fields.len(), if color { 11 } else { 9 });
    assert_eq!(
        (fields[0], fields[1], fields[2], fields[3]),
        ("00", "00000003", "D000", "OK")
    );
    let mut heap = vec![0x5a; 4096];
    heap[254..256].copy_from_slice(&[160, 0]);
    heap[256..416].fill(0);
    heap[0x10c..0x110].copy_from_slice(&[25, 6, 11, 0x20]);
    assert_heap_snapshot(fields[4], &heap, "Sprite shadow");
    let mut vram = vec![0xa7; 8192];
    vram[0x1800..].fill(0);
    assert_heap_snapshot(
        fields[if color { 6 } else { 5 }],
        &vram,
        "unchanged tile data/cleared maps",
    );
    if color {
        assert_heap_snapshot(fields[5], &[0x6b; 4096], "untouched bank2");
        assert_heap_snapshot(fields[7], &[0xb8; 8192], "untouched VRAM1");
    }
    assert_heap_snapshot(
        fields[if color { 9 } else { 7 }],
        &[0; 160],
        "unflushed hardware OAM",
    );
    assert_eq!(fields[if color { 10 } else { 8 }], "11E4E4E4");
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn aggregate_csharp_values_and_native_fields_preserve_banks(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "Structs");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let assembly = resolver.assembly(0).unwrap();
    let token = (1..=assembly.metadata().method_defs.len())
        .map(|row| 0x06000000 | row as u32)
        .find(|&token| assembly.method_name(token).unwrap() == "Program::Copy")
        .unwrap();
    let body = assembly.method_body(token).unwrap();
    let instructions = koh_compile::cil::decode(body.il).unwrap();
    let pair = instructions
        .windows(2)
        .find(|pair| {
            pair[0].opcode == 0x71
                && pair[1].opcode == 0x81
                && pair[0].operand == pair[1].operand
                && pair[1].offset == pair[0].offset + 5
        })
        .expect("Roslyn Copy must snapshot and store the same Packet type");
    let base = (body.il.as_ptr() as usize)
        .checked_sub(images[0].as_ptr() as usize)
        .unwrap();
    assert_eq!(&images[0][base..base + body.il.len()], body.il);
    let offset = base + pair[0].offset as usize;
    let mut cpobj = images[0].clone();
    cpobj[offset] = 0x70;
    cpobj[offset + 5..offset + 10].fill(0);
    let copy_resolver = Resolver::new(&[&cpobj, &images[1]]).unwrap();
    assert!(
        koh_compile::cil::decode(
            copy_resolver
                .assembly(0)
                .unwrap()
                .method_body(token)
                .unwrap()
                .il
        )
        .unwrap()
        .iter()
        .any(|instruction| instruction.opcode == 0x70)
    );
    for (entry, resolver) in [
        (
            if color {
                "Program::BankedValues"
            } else {
                "Program::Values"
            },
            &resolver,
        ),
        (
            if color {
                "Program::BankedScalarsAndOverlap"
            } else {
                "Program::ScalarsAndOverlap"
            },
            &resolver,
        ),
        ("Program::NativeFields", &resolver),
        (
            if color {
                "Program::BankedConstructors"
            } else {
                "Program::Constructors"
            },
            &resolver,
        ),
        (
            if color {
                "Program::BankedScalarsAndOverlap"
            } else {
                "Program::ScalarsAndOverlap"
            },
            &copy_resolver,
        ),
    ] {
        let object = compile::object(resolver, Some(entry)).unwrap();
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                cgb: if color {
                    koh_link::CgbMode::Only
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
        assert_eq!(fields.len(), if color { 6 } else { 5 }, "{entry}");
        assert_eq!(
            (fields[0], fields[1], fields[2], fields[3]),
            ("00", "0000002A", "D000", "OK"),
            "{entry}"
        );
        let mut bank1 = vec![0x5a; 4096];
        let mut bank2 = vec![0x6b; 4096];
        if entry == "Program::NativeFields" {
            let memory = if color { &mut bank2 } else { &mut bank1 };
            memory[0] = 7;
            memory[2..4].copy_from_slice(&(-2i16).to_le_bytes());
            memory[4..8].copy_from_slice(&0x12345678u32.to_le_bytes());
            memory[8..16].copy_from_slice(&0x0123456789abcdefu64.to_le_bytes());
        }
        assert_heap_snapshot(fields[4], &bank1, entry);
        if color {
            assert_heap_snapshot(fields[5], &bank2, entry);
        }
    }
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn value_constructor_metadata_and_private_storage_diagnose(
    host: &Host,
    #[case] configuration: &str,
) {
    let (directory, images) = gameboy_images(host, configuration, "Structs");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let assembly = resolver.assembly(0).unwrap();
    let method = |name: &str| {
        (1..=assembly.metadata().method_defs.len())
            .map(|row| 0x06000000 | row as u32)
            .find(|&token| assembly.method_name(token).unwrap() == name)
            .unwrap()
    };
    let create = method("Program::Create");
    let body = assembly.method_body(create).unwrap();
    let base = (body.il.as_ptr() as usize)
        .checked_sub(images[0].as_ptr() as usize)
        .unwrap();
    assert_eq!(&images[0][base..base + body.il.len()], body.il);
    let il = koh_compile::cil::decode(body.il).unwrap();
    let construct = il
        .iter()
        .find(|i| i.opcode == 0x73)
        .expect("Factory must use newobj");
    let koh_compile::cil::Operand::Token(token) = construct.operand else {
        panic!("Constructor token")
    };
    let target = resolver.resolve_method(0, token).unwrap().definition;
    let definition = assembly.method(target.token).unwrap();
    let mut needle = definition.rva.to_le_bytes().to_vec();
    needle.extend(definition.impl_flags.to_le_bytes());
    needle.extend(definition.flags.to_le_bytes());
    let rows: Vec<_> = images[0]
        .windows(needle.len())
        .enumerate()
        .filter_map(|(i, bytes)| (bytes == needle).then_some(i))
        .collect();
    assert_eq!(rows.len(), 1);
    let declared = assembly.local_types(create).unwrap().len();
    assert!(declared < 4);
    let last = il.last().unwrap();
    assert_eq!(last.opcode, 0x2a);
    let constructors = method("Program::Constructors");
    let read = assembly.method_body(constructors).unwrap();
    let read_base = (read.il.as_ptr() as usize)
        .checked_sub(images[0].as_ptr() as usize)
        .unwrap();
    assert_eq!(&images[0][read_base..read_base + read.il.len()], read.il);
    let readonly = koh_compile::cil::decode(read.il)
        .unwrap()
        .into_iter()
        .find(|i| i.opcode == 0x7f)
        .expect("Readonly static struct read must take its address");
    let body = koh_compile::cil::decode(read.il).unwrap();
    assert!(
        body.iter().any(|branch| {
            matches!(branch.operand, koh_compile::cil::Operand::Target(target)
            if target < branch.offset && body.iter().any(|site|
                site.opcode == 0x73 && site.offset >= target && site.offset < branch.offset))
        }),
        "Constructor fixture must reuse a newobj site within a loop"
    );
    for (kind, expected) in [
        ("flags", "newobj requires"),
        ("target", "newobj requires"),
        ("private-local", "Invalid local/argument index"),
        ("readonly-store", "Readonly static field writes require"),
    ] {
        let mut bytes = images[0].clone();
        let selected = if kind == "readonly-store" {
            constructors
        } else {
            create
        };
        match kind {
            "flags" => bytes[rows[0] + 6..rows[0] + 8]
                .copy_from_slice(&(definition.flags & !0x1000).to_le_bytes()),
            "target" => bytes
                [base + construct.offset as usize + 1..base + construct.offset as usize + 5]
                .copy_from_slice(&method("Program::Values").to_le_bytes()),
            "private-local" => bytes[base + last.offset as usize] = 0x06 + declared as u8,
            "readonly-store" => bytes[read_base + readonly.offset as usize] = 0x80,
            _ => unreachable!(),
        }
        let checked = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let resolver = Resolver::new(&[&bytes, &images[1]])?;
            koh_compile::lower::assembly_method(
                &resolver,
                koh_compile::resolve::Definition {
                    assembly: 0,
                    token: selected,
                },
            )
        }))
        .expect("Malformed constructor input must not panic")
        .unwrap_err();
        assert!(
            checked.contains(expected) && checked.contains("IL_"),
            "{kind}: {checked}"
        );
    }
    for (entry, expected) in [("Program::UnsupportedGeneric", "Generic call instantiation")] {
        let error = compile::object(&resolver, Some(entry)).unwrap_err();
        assert!(
            error.contains(expected) && error.contains(entry) && error.contains("IL_"),
            "{error}"
        );
    }
    let object = compile::object(&resolver, Some("Program::ReferenceConstructor")).unwrap();
    let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
    let linked = koh_link::link(&[object], &Default::default()).unwrap();
    let report = execute_mode(
        host,
        directory.path(),
        &linked,
        "__koh_exit",
        "--native-heap",
    );
    let fields: Vec<_> = report.split_whitespace().collect();
    assert_eq!(&fields[..4], &["00", "00000001", "D000", "OK"]);
    let class = assembly
        .metadata()
        .type_defs
        .iter()
        .position(|t| {
            assembly.metadata().strings.get(t.type_name).unwrap() == "ReferenceConstructed"
        })
        .unwrap();
    let symbol = koh_compile::sm83::class_symbol(Some(koh_compile::resolve::Definition {
        assembly: 0,
        token: 0x02000001 + class as u32,
    }));
    let descriptor = linked
        .symbols
        .iter()
        .find(|s| s.name == symbol)
        .unwrap()
        .value as u16;
    let mut heap = vec![0x5a; 4096];
    heap[..2].copy_from_slice(&descriptor.to_le_bytes());
    heap[2..6].copy_from_slice(&1i32.to_le_bytes());
    assert_heap_snapshot(fields[4], &heap, "ReferenceConstructor");
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn aligned_static_arrays_have_aligned_mutable_payloads(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let directory = tempfile::Builder::new()
        .prefix("koh aligned arrays ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let source = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Alignment");
    for name in ["Alignment.csproj", "Program.cs", "Edges.cs"] {
        std::fs::copy(source.join(name), directory.path().join(name)).unwrap();
    }
    let result = dotnet()
        .arg("build")
        .arg(directory.path().join("Alignment.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .arg(format!("-p:KohRoot={}", root.display()))
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}{}",
        String::from_utf8_lossy(&result.stdout),
        String::from_utf8_lossy(&result.stderr)
    );
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let images =
        ["Alignment.dll", "Koh.GameBoy.dll"].map(|name| std::fs::read(output.join(name)).unwrap());
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let object = compile::object(
        &resolver,
        Some(if color {
            "Program::Banked"
        } else {
            "Program::Main"
        }),
    )
    .unwrap();
    let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
    let linked = koh_link::link(
        &[object],
        &koh_link::LinkOptions {
            cgb: if color {
                koh_link::CgbMode::Only
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
    assert_eq!(
        (fields[0], fields[1], fields[2], fields[3]),
        ("00", "0000BE4C", "D000", "OK")
    );
    let mut heap = vec![0x5a; 4096];
    heap[..3].copy_from_slice(&[1, 0, 42]);
    heap[0xfe..0x100].copy_from_slice(&160u16.to_le_bytes());
    heap[0x100..0x1a0].fill(0);
    heap[0x100] = 11;
    heap[0x19f] = 22;
    heap[0x1ae..0x1b0].copy_from_slice(&3u16.to_le_bytes());
    heap[0x1b0..0x1b6].copy_from_slice(&[0x34, 0x12, 0, 0, 0xcd, 0xab]);
    assert_heap_snapshot(fields[4], &heap, "aligned payloads");
    if color {
        assert_heap_snapshot(fields[5], &[0x6b; 4096], "untouched bank2");
    }
    for (entry, fault, result) in [
        ("EmptyAligned::Main", 0, "00000000"),
        ("OverflowAligned::Main", 5, "00000000"),
        ("AliasAligned::Main", 0, "0000002A"),
        ("ScalarAligned::Main", 0, "0000002A"),
    ] {
        let object = compile::object(&resolver, Some(entry)).unwrap();
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                cgb: if color {
                    koh_link::CgbMode::Only
                } else {
                    koh_link::CgbMode::Preserve
                },
                ..Default::default()
            },
        )
        .unwrap();
        let stop = if fault == 0 {
            "__koh_exit"
        } else {
            "__koh_abort"
        };
        let text = execute_mode(host, directory.path(), &linked, stop, "--native-heap");
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(fields.len(), if color { 6 } else { 5 }, "{entry}");
        assert_eq!(fields[0], format!("{fault:02X}"), "{entry}");
        assert_eq!(fields[3], "OK", "{entry}: static guard");
        if fault == 0 {
            assert_eq!(
                (fields[1], fields[2], fields[3]),
                (result, "D000", "OK"),
                "{entry}"
            );
        }
        let mut expected = vec![0x5a; 4096];
        match entry {
            "EmptyAligned::Main" => expected[4094..].fill(0),
            "AliasAligned::Main" => {
                expected[0xfe..0x100].copy_from_slice(&8u16.to_le_bytes());
                expected[0x100..0x108].fill(0);
                expected[0x100] = 42;
            }
            _ => {}
        }
        assert_heap_snapshot(fields[4], &expected, entry);
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
    let error = compile::object(&resolver, Some("FactoryAligned::Main")).unwrap_err();
    assert!(
        error.contains("visible newarr") && error.contains("IL_"),
        "{error}"
    );
}

#[rstest]
#[case("Debug", "Library")]
#[case("Release", "Library")]
#[case("Debug", "Exe")]
#[case("Release", "Exe")]
fn native_driver_emits_one_linkable_deterministic_elf(
    host: &Host,
    #[case] configuration: &str,
    #[case] output: &str,
) {
    let directory = tempfile::Builder::new()
        .prefix("koh native driver with spaces ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let assembly_path = build(directory.path(), configuration, output, false);
    let bytes = std::fs::read(&assembly_path).unwrap();
    let resolver = Resolver::new(&[bytes.as_slice()]).unwrap();
    let object = compile::object(&resolver, None).unwrap();
    let elf = koh_objects::write_elf(&object).unwrap();
    assert_eq!(
        elf,
        koh_objects::write_elf(&compile::object(&resolver, None).unwrap()).unwrap()
    );
    let output_path = directory.path().join("native game object.o");
    for _ in 0..2 {
        let output = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
            .arg(&assembly_path)
            .arg("-o")
            .arg(&output_path)
            .arg("--reference")
            .arg(&assembly_path)
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert_eq!(std::fs::read(&output_path).unwrap(), elf);
        assert_eq!(std::fs::read(&assembly_path).unwrap(), bytes);
    }
    let object = koh_objects::read_elf(&elf).unwrap();
    let linked = koh_link::link(&[object], &koh_link::LinkOptions::default()).unwrap();
    assert_eq!(linked.rom[0x100], 0);
    assert_eq!(linked.rom[0x101], 0xc3);
    for name in ["__koh_start", "__koh_exit", "__koh_abort"] {
        assert!(
            linked.symbols.iter().any(|s| s.name == name),
            "Missing {name}"
        );
    }
    assert_eq!(linked.rom.len(), 32768);
    assert_eq!(
        execute(host, directory.path(), &linked, "__koh_exit"),
        "00 0000002A D000 OK"
    );
    for (entry, expected) in [
        ("Program::Other", "00 00000000 D000 OK"),
        ("Program::Word", "00 89ABCDEF D000 OK"),
    ] {
        let result = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
            .arg(&assembly_path)
            .arg("-o")
            .arg(&output_path)
            .args(["--entry", entry])
            .output()
            .unwrap();
        assert!(
            result.status.success(),
            "{}",
            String::from_utf8_lossy(&result.stderr)
        );
        let object = koh_objects::read_elf(&std::fs::read(&output_path).unwrap()).unwrap();
        let linked = koh_link::link(&[object], &koh_link::LinkOptions::default()).unwrap();
        assert_eq!(
            execute(host, directory.path(), &linked, "__koh_exit"),
            expected
        );
    }
}

fn execute(host: &Host, directory: &Path, linked: &koh_link::LinkResult, stop: &str) -> String {
    execute_mode(host, directory, linked, stop, "--native")
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn primitive_arrays_are_zeroed_checked_and_bank_safe(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let directory = tempfile::Builder::new()
        .prefix("koh primitive arrays ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Arrays");
    for file in ["Arrays.csproj", "Program.cs"] {
        std::fs::copy(fixture.join(file), directory.path().join(file)).unwrap();
    }
    let output = dotnet()
        .arg("build")
        .arg(directory.path().join("Arrays.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .arg(format!("-p:KohRoot={}", root.display()))
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let object_path = directory.path().join("arrays.o");
    for (entry, fault) in [
        (if color { "Banked" } else { "Main" }, 0),
        (if color { "BankedTypes" } else { "Types" }, 0),
        ("ObjectArray", 0),
        ("Multidimensional", 0),
        ("NullArray", 4),
        ("Negative", 5),
        ("TooLarge", 5),
        ("Truncated", 5),
        ("NegativeIndex", 6),
        ("HighIndex", 6),
        ("EmptyIndex", 6),
        ("Exhausted", 5),
    ] {
        let result = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
            .arg(output.join("Arrays.dll"))
            .arg("-r")
            .arg(output.join("Koh.GameBoy.dll"))
            .args(["--entry", &format!("Program::{entry}")])
            .arg("-o")
            .arg(&object_path)
            .output()
            .unwrap();
        assert!(
            result.status.success(),
            "{entry}: {}",
            String::from_utf8_lossy(&result.stderr)
        );
        let object = koh_objects::read_elf(&std::fs::read(&object_path).unwrap()).unwrap();
        let options = koh_link::LinkOptions {
            cgb: if color {
                koh_link::CgbMode::Only
            } else {
                koh_link::CgbMode::Preserve
            },
            ..Default::default()
        };
        let linked = koh_link::link(&[object], &options).unwrap();
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            if fault == 0 {
                "__koh_exit"
            } else {
                "__koh_abort"
            },
            "--native-heap",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(
            fields.len(),
            if color { 6 } else { 5 },
            "{entry}: malformed host output"
        );
        assert_eq!(fields[0], format!("{fault:02X}"), "{entry}");
        assert_eq!(fields[3], "OK", "{entry}: corrupted frame guards");
        let mut heap = vec![0x5a; 0x1000];
        if fault == 0 {
            assert_eq!(
                (fields[1], fields[2]),
                (
                    if entry == "ObjectArray" {
                        "00000002"
                    } else if entry == "Multidimensional" {
                        "00000004"
                    } else {
                        "0000002A"
                    },
                    "D000"
                ),
                "{entry}"
            );
            let mut expected = vec![];
            let mut array = |count: u16, data: &[u8]| {
                expected.extend_from_slice(&count.to_le_bytes());
                expected.extend_from_slice(data);
            };
            if entry == "ObjectArray" {
                array(2, &[0; 4]);
            } else if entry == "Multidimensional" {
                let mut dimensions_and_data = [0; 20];
                dimensions_and_data[0] = 2;
                dimensions_and_data[2] = 2;
                array(4, &dimensions_and_data);
            } else if matches!(entry, "Types" | "BankedTypes") {
                array(1, &[1]);
                array(1, &0xf123u16.to_le_bytes());
                array(1, &(-1234i16).to_le_bytes());
                array(1, &0xfedcba98u32.to_le_bytes());
                array(1, &0xfedcba9876543210u64.to_le_bytes());
                array(1, &(-1234i16).to_le_bytes());
                array(1, &0xfedcu16.to_le_bytes());
            } else {
                let values: Vec<_> = [38i32, 39, 41, 41, 42]
                    .into_iter()
                    .flat_map(i32::to_le_bytes)
                    .collect();
                array(5, &values);
                array(3, &[0, 0xab, 0]);
                array(2, &[0, 0xfe]);
                array(2, &[0, 0, 0xdc, 0xfe]);
                let mut wide = vec![0; 8];
                wide.extend_from_slice(&0x1122334455667788u64.to_le_bytes());
                array(2, &wide);
                array(0, &[]);
                array(0, &[]);
            }
            heap[..expected.len()].copy_from_slice(&expected);
        } else if entry == "Exhausted" {
            heap.fill(0);
            heap[..2].copy_from_slice(&4094u16.to_le_bytes());
            heap[2] = 0x7b;
        } else if matches!(entry, "NegativeIndex" | "HighIndex" | "EmptyIndex") {
            let count = if entry == "EmptyIndex" { 0u16 } else { 2u16 };
            let bytes = if entry == "NegativeIndex" {
                8
            } else if entry == "HighIndex" {
                2
            } else {
                0
            };
            heap[..bytes + 2].fill(0);
            heap[..2].copy_from_slice(&count.to_le_bytes());
        }
        assert_heap_snapshot(fields[4], &heap, entry);
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 0x1000], entry);
        }
    }
    for entry in ["FloatArray", "NestedArray", "RankThree"] {
        let result = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
            .arg(output.join("Arrays.dll"))
            .arg("-r")
            .arg(output.join("Koh.GameBoy.dll"))
            .args(["--entry", &format!("Program::{entry}")])
            .arg("-o")
            .arg(&object_path)
            .output()
            .unwrap();
        let error = String::from_utf8_lossy(&result.stderr);
        assert!(
            !result.status.success()
                && error.contains("IL_")
                && (error.contains("primitive")
                    || error.contains("integer")
                    || error.contains("Instance")
                    || error.contains("rank 3")),
            "{entry}: {error}"
        );
    }
}

fn assert_heap_snapshot(text: &str, expected: &[u8], entry: &str) {
    assert_eq!(
        text.len(),
        expected.len() * 2,
        "{entry}: heap snapshot length"
    );
    for (index, byte) in expected.iter().enumerate() {
        let actual = u8::from_str_radix(&text[index * 2..index * 2 + 2], 16).unwrap();
        assert_eq!(actual, *byte, "{entry}: heap offset {index:04X}");
    }
}

fn slide_2048(input: &[u8; 16], direction: usize) -> ([u8; 16], u16) {
    let mut output = [0; 16];
    let mut score = 0u16;
    let index = |line: usize, offset: usize| match direction {
        0 => line * 4 + offset,
        1 => line * 4 + 3 - offset,
        2 => offset * 4 + line,
        _ => (3 - offset) * 4 + line,
    };
    for line in 0..4 {
        let mut tiles = (0..4)
            .map(|i| input[index(line, i)])
            .filter(|&v| v != 0)
            .peekable();
        let mut position = 0;
        while let Some(mut value) = tiles.next() {
            if tiles.peek() == Some(&value) {
                tiles.next();
                value += 1;
                score = score.wrapping_add((1u32 << value) as u16);
            }
            output[index(line, position)] = value;
            position += 1;
        }
    }
    (output, score)
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn real_2048_board_and_stack_buffers_execute(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let directory = tempfile::Builder::new()
        .prefix("koh real 2048 board ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Board");
    for name in ["Board.csproj", "Program.cs"] {
        std::fs::copy(fixture.join(name), directory.path().join(name)).unwrap();
    }
    let built = dotnet()
        .arg("build")
        .arg(directory.path().join("Board.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .arg(format!("-p:KohRoot={}", root.display()))
        .output()
        .unwrap();
    assert!(
        built.status.success(),
        "{}{}",
        String::from_utf8_lossy(&built.stdout),
        String::from_utf8_lossy(&built.stderr)
    );
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let initial = [1u8, 1, 1, 1, 1, 1, 2, 0, 0, 2, 0, 2, 3, 0, 3, 3];
    assert_eq!(
        slide_2048(&initial, 0),
        ([2, 2, 0, 0, 2, 2, 0, 0, 3, 0, 0, 0, 4, 3, 0, 0], 36)
    );
    for (direction, name) in [
        "Left",
        "Right",
        "Up",
        "Down",
        "Scratch",
        "Zeroed",
        "Empty",
        "Enums",
        "PointerReference",
        "ExhaustedStack",
    ]
    .into_iter()
    .enumerate()
    {
        let entry = if color && (direction < 4 || name == "Enums") {
            format!("Banked{name}")
        } else {
            name.into()
        };
        let object_path = directory.path().join("board.o");
        let result = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
            .arg(output.join("Board.dll"))
            .arg("-r")
            .arg(output.join("Koh.GameBoy.dll"))
            .args(["--entry", &format!("Program::{entry}")])
            .arg("-o")
            .arg(&object_path)
            .output()
            .unwrap();
        assert!(
            result.status.success(),
            "{name}: {}",
            String::from_utf8_lossy(&result.stderr)
        );
        let object = koh_objects::read_elf(&std::fs::read(&object_path).unwrap()).unwrap();
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                cgb: if color {
                    koh_link::CgbMode::Only
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
            if name == "ExhaustedStack" {
                "__koh_abort"
            } else {
                "__koh_exit"
            },
            "--native-stack",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(fields.len(), if color { 6 } else { 5 });
        assert_eq!(
            fields[0],
            if name == "ExhaustedStack" { "01" } else { "00" },
            "{name}"
        );
        assert_eq!(fields[3], "OK", "{name}");
        if name != "ExhaustedStack" {
            assert_eq!(fields[2], "D000", "{name}");
        }
        let mut heap = vec![0x5a; 4096];
        let expected = if direction < 4 {
            let (board, score) = slide_2048(&initial, direction);
            heap[..2].copy_from_slice(&16u16.to_le_bytes());
            heap[2..18].copy_from_slice(&board);
            heap[18..20].copy_from_slice(&16u16.to_le_bytes());
            heap[20..36].copy_from_slice(&initial);
            u32::from(score)
                | (u32::from(board != initial) << 16)
                | (u32::from(board.iter().any(|&v| v >= 11)) << 17)
                | (1 << 18)
        } else {
            if name == "Enums" {
                heap[..5].copy_from_slice(&[3, 0, 0xff, 0xfe, 0xfe]);
                heap[5..7].copy_from_slice(&1u16.to_le_bytes());
                heap[7..15].copy_from_slice(&0xfedcba9876543210u64.to_le_bytes());
            }
            match name {
                "Scratch" => (20..=28).sum(),
                "Zeroed" => 0xab + 0xcd,
                "PointerReference" => 0x12345678,
                _ => 42,
            }
        };
        if name != "ExhaustedStack" {
            assert_eq!(fields[1], format!("{expected:08X}"), "{name}");
        }
        assert_heap_snapshot(fields[4], &heap, name);
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], name);
        }
    }
    for entry in ["Oversized", "Dynamic"] {
        let result = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
            .arg(output.join("Board.dll"))
            .arg("-r")
            .arg(output.join("Koh.GameBoy.dll"))
            .args(["--entry", &format!("Program::{entry}")])
            .arg("-o")
            .arg(directory.path().join("invalid.o"))
            .output()
            .unwrap();
        let error = String::from_utf8_lossy(&result.stderr);
        assert!(
            !result.status.success()
                && error.contains("IL_")
                && (error.contains("stack") || error.contains("Stack")),
            "{entry}: {error}"
        );
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn rva_literals_initialize_distinct_mutable_arrays(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    use koh_compile::{assembly::Assembly, cil};
    let directory = tempfile::Builder::new()
        .prefix("koh RVA arrays ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/ArrayLiterals");
    std::fs::create_dir(directory.path().join("Library")).unwrap();
    for name in [
        "ArrayLiterals.csproj",
        "Program.cs",
        "Library/ArrayData.csproj",
        "Library/Data.cs",
    ] {
        std::fs::copy(fixture.join(name), directory.path().join(name)).unwrap();
    }
    let output = dotnet()
        .arg("build")
        .arg(directory.path().join("ArrayLiterals.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .arg(format!("-p:KohRoot={}", root.display()))
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let original = std::fs::read(output.join("ArrayLiterals.dll")).unwrap();
    let image = Assembly::parse(&original).unwrap();
    let probe = (1..=image.metadata().method_defs.len())
        .map(|r| 0x06000000 | r as u32)
        .find(|&t| image.method_name(t).unwrap() == "Program::Probe")
        .unwrap();
    let body = image.method_body(probe).unwrap();
    let base = body.il.as_ptr() as usize - original.as_ptr() as usize;
    let il = cil::decode(body.il).unwrap();
    let allocation = il.windows(2).find(|pair| pair[1].opcode == 0x8d).unwrap();
    assert_eq!(
        allocation[0].opcode, 0x19,
        "Probe must allocate three elements"
    );
    assert!(
        il.iter().any(|i| i.opcode == 0xd0),
        "Roslyn must emit a field token"
    );
    for (kind, fault, expected_result) in [
        ("Main", 0, 42),
        ("Probe", 0, 3),
        ("Small", 0, 2),
        ("Empty", 0, 0),
        ("Large", 7, 0),
        ("Null", 4, 0),
    ] {
        let mut bytes = original.clone();
        if matches!(kind, "Small" | "Empty" | "Large") {
            bytes[base + allocation[0].offset as usize] = match kind {
                "Small" => 0x18,
                "Empty" => 0x16,
                _ => 0x1a,
            };
        } else if kind == "Null" {
            let position = base + allocation[1].offset as usize;
            bytes[position..position + 5].copy_from_slice(&[0x26, 0x14, 0, 0, 0]);
        }
        let path = directory.path().join("mutated fixture.dll");
        std::fs::write(&path, &bytes).unwrap();
        let object = directory.path().join("array literals.o");
        let entry = match (kind == "Main", color) {
            (true, false) => "Main",
            (true, true) => "Banked",
            (false, false) => "Probe",
            (false, true) => "BankedProbe",
        };
        let result = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
            .arg(&path)
            .arg("-r")
            .arg(output.join("ArrayData.dll"))
            .arg("-r")
            .arg(output.join("Koh.GameBoy.dll"))
            .args(["--entry", &format!("Program::{entry}")])
            .arg("-o")
            .arg(&object)
            .output()
            .unwrap();
        assert!(
            result.status.success(),
            "{kind}: {}",
            String::from_utf8_lossy(&result.stderr)
        );
        let object = koh_objects::read_elf(&std::fs::read(&object).unwrap()).unwrap();
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                cgb: if color {
                    koh_link::CgbMode::Only
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
            if fault == 0 {
                "__koh_exit"
            } else {
                "__koh_abort"
            },
            "--native-heap",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(fields.len(), if color { 6 } else { 5 });
        assert_eq!(fields[0], format!("{fault:02X}"), "{kind}");
        assert_eq!(fields[3], "OK", "{kind}");
        if fault == 0 {
            assert_eq!(
                (fields[1], fields[2]),
                (format!("{expected_result:08X}").as_str(), "D000"),
                "{kind}"
            );
        }
        let mut expected = vec![];
        let mut array = |count: u16, data: &[u8]| {
            expected.extend_from_slice(&count.to_le_bytes());
            expected.extend_from_slice(data);
        };
        array(4, &[21, 22, if kind == "Main" { 77 } else { 23 }, 24]);
        if kind == "Main" {
            let mut data = vec![
                6, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
            ];
            data[1] = 99;
            array(18, &data);
            data[1] = 2;
            array(18, &data);
            array(3, &[0xff, 2, 0xfd]);
            array(
                3,
                &[-1i16, 0x1234, -1234]
                    .into_iter()
                    .flat_map(i16::to_le_bytes)
                    .collect::<Vec<_>>(),
            );
            array(
                3,
                &[0xfedcu16, 1, 2]
                    .into_iter()
                    .flat_map(u16::to_le_bytes)
                    .collect::<Vec<_>>(),
            );
            array(
                3,
                &[-1i32, 0x12345678, i32::MIN]
                    .into_iter()
                    .flat_map(i32::to_le_bytes)
                    .collect::<Vec<_>>(),
            );
            array(
                3,
                &[0xfedcba98u32, 1, u32::MAX]
                    .into_iter()
                    .flat_map(u32::to_le_bytes)
                    .collect::<Vec<_>>(),
            );
            array(
                3,
                &[0x1122334455667788i64, -2, i64::MIN]
                    .into_iter()
                    .flat_map(i64::to_le_bytes)
                    .collect::<Vec<_>>(),
            );
            array(
                3,
                &[0xfedcba9876543210u64, 1, u64::MAX]
                    .into_iter()
                    .flat_map(u64::to_le_bytes)
                    .collect::<Vec<_>>(),
            );
            array(
                3,
                &[65u16, 0xf123, 0]
                    .into_iter()
                    .flat_map(u16::to_le_bytes)
                    .collect::<Vec<_>>(),
            );
            array(4, &[1, 0, 1, 0]);
        } else if kind != "Null" {
            let data: &[u8] = match kind {
                "Probe" => &[1, 2, 3],
                "Small" => &[1, 2],
                "Empty" => &[],
                _ => &[0, 0, 0, 0],
            };
            array(data.len() as u16, data);
        }
        let mut heap = vec![0x5a; 4096];
        heap[..expected.len()].copy_from_slice(&expected);
        assert_heap_snapshot(fields[4], &heap, kind);
        if color {
            assert_heap_snapshot(fields[5], &[0x6b; 4096], kind);
        }
    }
    let metadata = image.metadata();
    let helper = metadata
        .member_refs
        .iter()
        .find(|m| metadata.strings.get(m.name).unwrap() == "InitializeArray")
        .unwrap();
    let signature = metadata.blobs.get(helper.signature).unwrap();
    let owner = &metadata.type_refs[helper.class.row as usize - 1];
    assert_eq!(helper.class.table, Some(clrmeta::TableId::TypeRef));
    assert_eq!(
        owner.resolution_scope.table,
        Some(clrmeta::TableId::AssemblyRef)
    );
    for mutation in ["has-this", "handle-class", "wrong-scope"] {
        let (needle, offset, replacement) = if mutation == "wrong-scope" {
            let mut row = ((owner.resolution_scope.row as u16) << 2 | 2)
                .to_le_bytes()
                .to_vec();
            assert!(owner.type_name < 65536 && owner.type_namespace < 65536);
            row.extend_from_slice(&(owner.type_name as u16).to_le_bytes());
            row.extend_from_slice(&(owner.type_namespace as u16).to_le_bytes());
            let reference = metadata
                .assembly_refs
                .iter()
                .position(|a| metadata.strings.get(a.name).unwrap() == "ArrayData")
                .unwrap() as u16
                + 1;
            (row, 0, ((reference << 2) | 2).to_le_bytes().to_vec())
        } else {
            assert_eq!(signature[0], 0);
            assert_eq!(signature[signature.len() - 2], 0x11);
            (
                signature.to_vec(),
                if mutation == "has-this" {
                    0
                } else {
                    signature.len() - 2
                },
                vec![if mutation == "has-this" { 0x20 } else { 0x12 }],
            )
        };
        let positions: Vec<_> = original
            .windows(needle.len())
            .enumerate()
            .filter_map(|(i, b)| (b == needle).then_some(i))
            .collect();
        assert_eq!(positions.len(), 1, "Unique helper metadata mutation");
        let mut bytes = original.clone();
        let start = positions[0] + offset;
        bytes[start..start + replacement.len()].copy_from_slice(&replacement);
        let path = directory.path().join("bad helper.dll");
        std::fs::write(&path, bytes).unwrap();
        let result = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
            .arg(&path)
            .arg("-r")
            .arg(output.join("ArrayData.dll"))
            .arg("-r")
            .arg(output.join("Koh.GameBoy.dll"))
            .args(["--entry", "Program::Probe"])
            .arg("-o")
            .arg(directory.path().join("bad.o"))
            .output()
            .unwrap();
        let error = String::from_utf8_lossy(&result.stderr);
        assert!(
            !result.status.success() && error.contains("IL_") && !error.contains("panicked"),
            "{mutation}: {error}"
        );
        assert!(!directory.path().join("bad.o").exists());
    }
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn local_and_argument_references_preserve_ancestor_frames(
    host: &Host,
    #[case] configuration: &str,
) {
    let directory = tempfile::Builder::new()
        .prefix("koh ancestor frames ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/LocalReferences");
    for file in ["LocalReferences.csproj", "Program.cs"] {
        std::fs::copy(fixture.join(file), directory.path().join(file)).unwrap();
    }
    let output = dotnet()
        .arg("build")
        .arg(directory.path().join("LocalReferences.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let input = directory
        .path()
        .join(format!("bin/{configuration}/net10.0/LocalReferences.dll"));
    let path = directory.path().join("locals.o");
    let output = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
        .arg(input)
        .arg("-o")
        .arg(&path)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let object = koh_objects::read_elf(&std::fs::read(path).unwrap()).unwrap();
    let linked = koh_link::link(&[object], &koh_link::LinkOptions::default()).unwrap();
    assert_eq!(
        execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-poison"
        ),
        "00 0000002A D000 OK"
    );
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn type_and_module_initializers_execute_once_with_cycles(host: &Host, #[case] configuration: &str) {
    let directory = tempfile::Builder::new()
        .prefix("koh initializer order ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Initializers");
    for project in ["Entry", "Runtime"] {
        std::fs::create_dir(directory.path().join(project)).unwrap();
        for file in std::fs::read_dir(fixture.join(project)).unwrap() {
            let file = file.unwrap();
            std::fs::copy(
                file.path(),
                directory.path().join(project).join(file.file_name()),
            )
            .unwrap();
        }
    }
    let output = dotnet()
        .arg("build")
        .arg(directory.path().join("Entry/Entry.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let input = directory
        .path()
        .join(format!("Entry/bin/{configuration}/net10.0/Entry.dll"));
    let reference = directory
        .path()
        .join(format!("Runtime/bin/{configuration}/net10.0/Runtime.dll"));
    let object = directory.path().join("initialized.o");
    let output = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
        .arg(&input)
        .arg("-r")
        .arg(&reference)
        .arg("-o")
        .arg(&object)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let mut object = koh_objects::read_elf(&std::fs::read(object).unwrap()).unwrap();
    let linked = koh_link::link(&[object.clone()], &koh_link::LinkOptions::default()).unwrap();
    assert_eq!(
        execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-poison"
        ),
        "00 0000002A D000 OK"
    );
    let mut omitted = 0;
    for section in &mut object.sections {
        let mut calls = Vec::new();
        for patch in &section.patches {
            let koh_objects::RelocationExpr::Symbol(id) = &patch.expression else {
                continue;
            };
            let name = &object.symbols[id.0].name;
            if !name.contains("_mff") || name.contains('.') {
                continue;
            }
            assert_eq!(section.data[patch.offset - 1], 0x01);
            assert_eq!(section.data[patch.offset + 2], 0xcd);
            calls.push(patch.offset + 3);
            section.data[patch.offset + 2..patch.offset + 5].copy_from_slice(&[0xe8, 4, 0]);
            omitted += 1;
        }
        section
            .patches
            .retain(|patch| !calls.contains(&patch.offset));
    }
    assert!(omitted > 0);
    let mutated = koh_link::link(&[object], &koh_link::LinkOptions::default()).unwrap();
    assert_eq!(
        execute_mode(
            host,
            directory.path(),
            &mutated,
            "__koh_exit",
            "--native-poison"
        ),
        "00 00000001 D000 OK"
    );
    let bytes = std::fs::read(&input).unwrap();
    let runtime = std::fs::read(&reference).unwrap();
    let resolver = Resolver::new(&[bytes.as_slice(), runtime.as_slice()]).unwrap();
    assert!(
        compile::object(&resolver, Some("Program::.cctor"))
            .unwrap_err()
            .contains("Constructors")
    );
}

fn execute_mode(
    host: &Host,
    directory: &Path,
    linked: &koh_link::LinkResult,
    stop: &str,
    mode: &str,
) -> String {
    let path = directory.join("native game.gb");
    std::fs::write(&path, &linked.rom).unwrap();
    let exit = linked
        .symbols
        .iter()
        .find(|s| s.name == stop)
        .unwrap()
        .value;
    let output = dotnet()
        .arg(&host.dll)
        .args([mode, &format!("{exit:x}")])
        .arg(path)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    String::from_utf8(output.stdout).unwrap().trim().to_owned()
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn static_fields_and_byref_aliases_execute(host: &Host, #[case] configuration: &str) {
    let directory = tempfile::Builder::new()
        .prefix("koh static fields ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Statics");
    for project in ["Entry", "Runtime"] {
        std::fs::create_dir(directory.path().join(project)).unwrap();
        for file in std::fs::read_dir(fixture.join(project)).unwrap() {
            let file = file.unwrap();
            std::fs::copy(
                file.path(),
                directory.path().join(project).join(file.file_name()),
            )
            .unwrap();
        }
    }
    let result = dotnet()
        .arg("build")
        .arg(directory.path().join("Entry/Entry.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}{}",
        String::from_utf8_lossy(&result.stdout),
        String::from_utf8_lossy(&result.stderr)
    );
    let main_path = directory
        .path()
        .join(format!("Entry/bin/{configuration}/net10.0/Entry.dll"));
    let runtime_path = directory
        .path()
        .join(format!("Runtime/bin/{configuration}/net10.0/Runtime.dll"));
    let main = std::fs::read(&main_path).unwrap();
    let runtime = std::fs::read(&runtime_path).unwrap();
    let resolver = Resolver::new(&[main.as_slice(), runtime.as_slice()]).unwrap();
    let object = compile::object(&resolver, None).unwrap();
    let section = object
        .sections
        .iter()
        .find(|s| s.name == ".koh.statics")
        .unwrap();
    assert_eq!(section.kind, 4);
    assert_eq!(section.size, 31);
    assert!(section.data.is_empty());
    let path = directory.path().join("statics.o");
    let result = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
        .arg(&main_path)
        .arg("-r")
        .arg(&runtime_path)
        .arg("-o")
        .arg(&path)
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    let object = koh_objects::read_elf(&std::fs::read(path).unwrap()).unwrap();
    let linked = koh_link::link(&[object], &koh_link::LinkOptions::default()).unwrap();
    assert_eq!(
        execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-poison"
        ),
        "00 0000002A D000 OK"
    );
    let error = compile::object(&resolver, Some("Program::TooMany")).unwrap_err();
    assert!(
        error.contains("static storage") && error.contains("capacity"),
        "{error}"
    );
    let initialized = compile::object(&resolver, Some("Program::NeedsInitialization")).unwrap();
    let linked = koh_link::link(&[initialized], &koh_link::LinkOptions::default()).unwrap();
    assert_eq!(
        execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-poison"
        ),
        "00 00000011 D000 OK"
    );
    for (entry, diagnostic) in [("NeedsGenericStorage", "Generic")] {
        let error = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap_err();
        assert!(
            error.contains(diagnostic) && error.contains("IL_"),
            "{error}"
        );
    }
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn indirect_memory_widths_and_aliases_execute(host: &Host, #[case] configuration: &str) {
    let directory = tempfile::Builder::new()
        .prefix("koh memory ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Memory");
    for file in ["Memory.csproj", "Program.cs"] {
        std::fs::copy(fixture.join(file), directory.path().join(file)).unwrap();
    }
    let result = dotnet()
        .arg("build")
        .arg(directory.path().join("Memory.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}{}",
        String::from_utf8_lossy(&result.stdout),
        String::from_utf8_lossy(&result.stderr)
    );
    let bytes = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/Memory.dll")),
    )
    .unwrap();
    let resolver = Resolver::new(&[bytes.as_slice()]).unwrap();
    let object = compile::object(&resolver, None).unwrap();
    let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
    let linked = koh_link::link(&[object], &koh_link::LinkOptions::default()).unwrap();
    assert_eq!(
        execute(host, directory.path(), &linked, "__koh_exit"),
        "00 0000002A D000 OK"
    );
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn gameboy_register_and_region_intrinsics_execute(host: &Host, #[case] configuration: &str) {
    let directory = tempfile::Builder::new()
        .prefix("koh hardware ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Hardware");
    for file in ["Hardware.csproj", "Program.cs"] {
        std::fs::copy(fixture.join(file), directory.path().join(file)).unwrap();
    }
    let result = dotnet()
        .arg("build")
        .arg(directory.path().join("Hardware.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .arg(format!("-p:KohRoot={}", root.display()))
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}{}",
        String::from_utf8_lossy(&result.stdout),
        String::from_utf8_lossy(&result.stderr)
    );
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let main = std::fs::read(output.join("Hardware.dll")).unwrap();
    let runtime = std::fs::read(output.join("Koh.GameBoy.dll")).unwrap();
    let resolver = Resolver::new(&[main.as_slice(), runtime.as_slice()]).unwrap();
    let entry = compile::entry(&resolver, None).unwrap();
    let functions = compile::reachable(&resolver, entry).unwrap();
    assert_eq!(
        functions.len(),
        1,
        "Intrinsic desktop bodies must not be included"
    );
    let path = directory.path().join("hardware.o");
    let result = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
        .arg(output.join("Hardware.dll"))
        .arg("-r")
        .arg(output.join("Koh.GameBoy.dll"))
        .arg("-o")
        .arg(&path)
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    let object = koh_objects::read_elf(&std::fs::read(path).unwrap()).unwrap();
    let linked = koh_link::link(&[object], &koh_link::LinkOptions::default()).unwrap();
    assert_eq!(
        execute(host, directory.path(), &linked, "__koh_exit"),
        "00 0000002A D000 OK"
    );
}

#[rstest]
fn cli_references_and_native_stack_traps_execute(host: &Host) {
    let directory = tempfile::Builder::new()
        .prefix("koh cli project references ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Calls");
    for project in ["Entry", "Bridge", "Runtime"] {
        std::fs::create_dir(directory.path().join(project)).unwrap();
        for file in std::fs::read_dir(fixture.join(project)).unwrap() {
            let file = file.unwrap();
            std::fs::copy(
                file.path(),
                directory.path().join(project).join(file.file_name()),
            )
            .unwrap();
        }
    }
    let output = dotnet()
        .arg("build")
        .arg(directory.path().join("Entry/Entry.csproj"))
        .args(["-c", "Release", "--nologo", "-v:q", "-m:1"])
        .arg("-p:DisableTransitiveProjectReferences=true")
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let input = directory
        .path()
        .join("Entry/bin/Release/net10.0/Game.Calls.dll");
    let references = [
        directory
            .path()
            .join("Bridge/bin/Release/net10.0/Game.Bridge.dll"),
        directory
            .path()
            .join("Runtime/bin/Release/net10.0/Game.Runtime.dll"),
    ];
    let object_path = directory.path().join("whole game.o");
    for (entry, reverse) in [
        ("Program::Main", false),
        ("Program::Mutual", false),
        ("Program::Deep", false),
        ("Program::Main", true),
    ] {
        let mut command = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"));
        command
            .arg(&input)
            .arg("-o")
            .arg(&object_path)
            .args(["--entry", entry]);
        for index in if reverse { [1, 0] } else { [0, 1] } {
            command.arg("-r").arg(&references[index]);
        }
        let output = command.output().unwrap();
        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
        let object = koh_objects::read_elf(&std::fs::read(&object_path).unwrap()).unwrap();
        let linked = koh_link::link(&[object], &koh_link::LinkOptions::default()).unwrap();
        if entry == "Program::Deep" {
            let line = execute(host, directory.path(), &linked, "__koh_abort");
            assert!(line.starts_with("01 ") && line.ends_with(" OK"), "{line}");
        } else {
            let value = if entry == "Program::Mutual" {
                "00 00000001 D000 OK"
            } else {
                "00 0000002A D000 OK"
            };
            assert_eq!(
                execute(host, directory.path(), &linked, "__koh_exit"),
                value
            );
        }
    }
}

#[test]
fn cli_failures_preserve_inputs_and_previous_output() {
    let directory = tempfile::Builder::new()
        .prefix("koh cli failures ")
        .tempdir()
        .unwrap();
    let input = build(directory.path(), "Release", "Library", false);
    let bytes = std::fs::read(&input).unwrap();
    let output = directory.path().join("previous output.o");
    std::fs::write(&output, b"previous valid output").unwrap();
    let missing = directory.path().join("missing reference.dll");
    for (arguments, diagnostic) in [
        (vec!["--unknown"], "Unknown option"),
        (vec!["--entry"], "Missing"),
        (vec!["--entry", "Program::Unsupported"], "System.Runtime"),
        (vec!["--entry", "Program::UnsupportedThrow"], "opcode"),
        (vec!["--entry", "Program::NeedsArgument"], "parameter"),
        (
            vec!["--reference", missing.to_str().unwrap()],
            "missing reference.dll",
        ),
    ] {
        let result = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
            .arg(&input)
            .arg("-o")
            .arg(&output)
            .args(arguments)
            .output()
            .unwrap();
        assert!(!result.status.success());
        assert!(
            String::from_utf8_lossy(&result.stderr).contains(diagnostic),
            "{}",
            String::from_utf8_lossy(&result.stderr)
        );
        assert_eq!(std::fs::read(&output).unwrap(), b"previous valid output");
    }
    let result = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
        .arg(&input)
        .arg("-o")
        .arg(&input)
        .output()
        .unwrap();
    assert!(!result.status.success());
    assert!(String::from_utf8_lossy(&result.stderr).contains("distinct"));
    let output_directory = directory.path().join("existing output directory");
    std::fs::create_dir(&output_directory).unwrap();
    let result = std::process::Command::new(env!("CARGO_BIN_EXE_koh-compile"))
        .arg(&input)
        .arg("-o")
        .arg(&output_directory)
        .output()
        .unwrap();
    assert!(!result.status.success());
    assert!(output_directory.is_dir());
    assert!(!std::fs::read_dir(directory.path()).unwrap().any(|e| {
        e.unwrap()
            .file_name()
            .to_string_lossy()
            .starts_with(".koh-compile-")
    }));
    assert_eq!(std::fs::read(input).unwrap(), bytes);
}

#[test]
fn library_entry_is_unambiguous_and_checked() {
    let directory = tempfile::tempdir().unwrap();
    let path = build(directory.path(), "Release", "Library", true);
    let bytes = std::fs::read(path).unwrap();
    let resolver = Resolver::new(&[bytes.as_slice()]).unwrap();
    assert!(
        compile::object(&resolver, None)
            .unwrap_err()
            .contains("ambiguous")
    );
    compile::object(&resolver, Some("Program::Main")).unwrap();
    compile::object(&resolver, Some("Program::Other")).unwrap();
    for (name, diagnostic) in [
        ("NeedsArgument", "parameter"),
        ("LongEntry", "return"),
        ("Unsupported", "System.Runtime"),
        ("UnsupportedThrow", "opcode"),
    ] {
        let error = compile::object(&resolver, Some(&format!("Program::{name}"))).unwrap_err();
        assert!(error.contains(diagnostic), "{error}");
    }
}

#[test]
fn pending_hardware_contracts_never_become_desktop_code() {
    let directory = tempfile::Builder::new()
        .prefix("koh contract boundaries ")
        .tempdir()
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Contracts");
    let mut accepted = Vec::new();
    let app = directory.path().join("ordinary app");
    std::fs::create_dir(&app).unwrap();
    let app = std::fs::read(build(&app, "Release", "Library", false)).unwrap();
    for (variant, diagnostic) in [
        ("METHOD", "intrinsic"),
        ("PROPERTY", "intrinsic"),
        ("INTERRUPT", "Interrupt"),
        ("ADDRESS", "address"),
        ("SIGNATURE", "signature"),
        ("NULL", "payload"),
        ("DUPLICATE", "Multiple"),
        ("REGION", "signature"),
    ] {
        let project = directory.path().join(variant);
        std::fs::create_dir(&project).unwrap();
        for name in ["Contracts.csproj", "Program.cs"] {
            std::fs::copy(fixture.join(name), project.join(name)).unwrap();
        }
        let output = dotnet()
            .arg("build")
            .arg(project.join("Contracts.csproj"))
            .args(["-c", "Release", "--nologo", "-v:q", "-m:1"])
            .arg(format!("-p:KohRoot={}", root.display()))
            .arg(format!("-p:DefineConstants={variant}"))
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "{}\n{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
        let path = project.join("bin/Release/net10.0/Contracts.dll");
        let assembly = std::fs::read(path).unwrap();
        let runtime = std::fs::read(project.join("bin/Release/net10.0/Koh.GameBoy.dll")).unwrap();
        let resolver = Resolver::new(&[assembly.as_slice(), runtime.as_slice()]).unwrap();
        match compile::object(&resolver, None) {
            Ok(_) => accepted.push(variant),
            Err(error) => assert!(error.contains(diagnostic), "{variant}: {error}"),
        }
        if variant == "INTERRUPT" {
            let references =
                Resolver::new(&[app.as_slice(), assembly.as_slice(), runtime.as_slice()]).unwrap();
            if compile::object(&references, None).is_ok() {
                accepted.push("REFERENCE_INTERRUPT");
            }
        }
    }
    assert!(
        accepted.is_empty(),
        "Hardware contracts silently accepted: {accepted:?}"
    );
}

#[rstest]
fn nested_entry_owners_are_unambiguous(host: &Host) {
    let directory = tempfile::Builder::new()
        .prefix("koh nested entry ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let input = build_with(directory.path(), "Release", "Library", "NESTED");
    let bytes = std::fs::read(&input).unwrap();
    let resolver = Resolver::new(&[bytes.as_slice()]).unwrap();
    assert!(compile::object(&resolver, None).is_err());
    for (entry, value) in [
        ("Demo.Outer+Program::Main", "00 00000011 D000 OK"),
        ("Demo.Another+Program::Main", "00 00000017 D000 OK"),
    ] {
        let object = compile::object(&resolver, Some(entry)).unwrap();
        let linked = koh_link::link(&[object], &koh_link::LinkOptions::default()).unwrap();
        assert_eq!(
            execute(host, directory.path(), &linked, "__koh_exit"),
            value
        );
    }
    assert!(compile::object(&resolver, Some("Program::Main")).is_err());
}
