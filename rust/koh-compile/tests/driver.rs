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
