use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;
use std::path::{Path, PathBuf};
mod support;
use support::{Host, dotnet};

#[rstest::fixture]
#[once]
fn host() -> Host {
    support::build_host()
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
            assert_eq!((fields[1], fields[2]), ("0000002A", "D000"), "{entry}");
            let mut expected = vec![];
            let mut array = |count: u16, data: &[u8]| {
                expected.extend_from_slice(&count.to_le_bytes());
                expected.extend_from_slice(data);
            };
            if matches!(entry, "Types" | "BankedTypes") {
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
    for entry in [
        "FloatArray",
        "ObjectArray",
        "NestedArray",
        "Multidimensional",
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
        let error = String::from_utf8_lossy(&result.stderr);
        assert!(
            !result.status.success()
                && error.contains("IL_")
                && (error.contains("primitive")
                    || error.contains("integer")
                    || error.contains("Instance")),
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
        section.patches.retain(|patch| {
            let koh_objects::RelocationExpr::Symbol(id) = &patch.expression else {
                return true;
            };
            let name = &object.symbols[id.0].name;
            if !name.contains("_mff") || name.contains('.') {
                return true;
            }
            assert_eq!(section.data[patch.offset - 1], 0xcd);
            section.data[patch.offset - 1..patch.offset + 2].fill(0);
            omitted += 1;
            false
        });
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
        (vec!["--entry", "Program::Unsupported"], "opcode"),
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
        ("Unsupported", "opcode"),
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
