use super::{Host, gameboy_images, host};
use rstest::rstest;
use std::{ffi::OsString, path::Path, process::Command};

#[rstest::fixture]
#[once]
fn debugger() -> Host {
    super::support::build_fixture_host("DebuggerHost")
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn debugger_stops_on_native_csharp_source_and_inspects_machine_state(
    host: &Host,
    debugger: &Host,
    #[case] configuration: &str,
) {
    use koh_compile::{compile, resolve::Resolver};
    use serde_json::{Value, json};
    use std::{io::Write, process::Stdio};

    let (directory, images) = gameboy_images(host, configuration, "SourceDebug");
    let source = directory.path().join("Program.cs");
    let line = std::fs::read_to_string(&source)
        .unwrap()
        .lines()
        .position(|line| line.contains("Value = value + 0;"))
        .unwrap()
        + 1;
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
    let symbol_views: Vec<_> = symbols.iter().map(|s| Some(s.as_slice())).collect();
    let object =
        compile::object_with_debug(&resolver, Some("Program::Inspect"), &symbol_views).unwrap();
    let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
    let linked = koh_link::link(&[object], &Default::default()).unwrap();
    let breakpoint = linked
        .lines
        .iter()
        .find(|p| p.file == source.to_str().unwrap() && p.line == line as u32)
        .unwrap();
    let rom = directory.path().join("debug game.gb");
    let kdbg = directory.path().join("debug game.kdbg");
    std::fs::write(&rom, &linked.rom).unwrap();
    std::fs::write(&kdbg, linked.kdbg().unwrap()).unwrap();
    let mut requests = Vec::new();
    for (command, arguments) in [
        ("launch", json!({"program": rom, "debugInfo": kdbg})),
        (
            "setBreakpoints",
            json!({"source": {"path": source}, "breakpoints": [{"line": line}]}),
        ),
        ("continue", json!({"threadId": 1})),
        ("advance", json!({})),
        ("stackTrace", json!({"threadId": 1})),
        ("scopes", json!({"frameId": 0})),
        ("variables", json!({"variablesReference": 1})),
        ("variables", json!({"variablesReference": 3})),
        (
            "disassemble",
            json!({"memoryReference": format!("0x{:04X}", breakpoint.address), "instructionCount": 4}),
        ),
        (
            "setFunctionBreakpoints",
            json!({"breakpoints": [{"name": "__koh_exit"}]}),
        ),
        ("continue", json!({"threadId": 1})),
        ("advance", json!({})),
        ("variables", json!({"variablesReference": 1})),
    ] {
        let request = json!({"seq": requests.len() + 1, "type": "request", "command": command, "arguments": arguments});
        requests.push(request.to_string());
    }
    let mut child = super::dotnet()
        .arg(&debugger.dll)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child
        .stdin
        .take()
        .unwrap()
        .write_all(requests.join("\n").as_bytes())
        .unwrap();
    let output = child.wait_with_output().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let replies: Vec<Value> = String::from_utf8(output.stdout)
        .unwrap()
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect();
    assert_eq!(replies.len(), requests.len(), "{replies:#?}");
    for (index, reply) in replies.iter().enumerate() {
        if index != 3 && index != 11 {
            assert_eq!(reply["success"], true, "{reply}");
        }
    }
    assert_eq!(replies[1]["body"]["breakpoints"][0]["verified"], true);
    assert_eq!(replies[1]["body"]["breakpoints"][0]["line"], line);
    assert_eq!(replies[3]["reason"], "Breakpoint");
    assert_eq!(replies[3]["bootMapped"], false);
    assert_eq!(replies[3]["pc"], breakpoint.address);
    assert!(replies[3]["sp"].as_u64().unwrap() < 0xD000);
    assert_eq!(replies[3]["stack"].as_str().unwrap().len(), 128);
    let frame = &replies[4]["body"]["stackFrames"][0];
    assert_eq!(frame["source"]["path"], source.to_str().unwrap());
    assert_eq!(frame["line"], line);
    assert!(
        replies[5]["body"]["scopes"]
            .as_array()
            .unwrap()
            .iter()
            .any(|s| s["name"] == "Registers")
    );
    let registers = replies[6]["body"]["variables"].as_array().unwrap();
    for (name, value) in [
        ("PC", breakpoint.address as u64),
        ("SP", replies[3]["sp"].as_u64().unwrap()),
    ] {
        assert!(
            registers
                .iter()
                .any(|r| r["name"] == name && r["value"] == format!("${value:04X}"))
        );
    }
    assert!(
        replies[7]["body"]["variables"]
            .as_array()
            .unwrap()
            .iter()
            .any(|s| s["name"]
                .as_str()
                .is_some_and(|s| s.contains("Program::Probe")))
    );
    let instructions = replies[8]["body"]["instructions"].as_array().unwrap();
    assert_eq!(instructions.len(), 4);
    assert_eq!(
        instructions[0]["address"],
        format!("0x{:04X}", breakpoint.address)
    );
    assert!(
        instructions
            .iter()
            .all(|i| !i["instructionBytes"].as_str().unwrap().is_empty())
    );
    for instruction in instructions {
        let address = usize::from_str_radix(
            instruction["address"]
                .as_str()
                .unwrap()
                .trim_start_matches("0x"),
            16,
        )
        .unwrap();
        let bytes: Vec<_> = instruction["instructionBytes"]
            .as_str()
            .unwrap()
            .split_whitespace()
            .map(|b| u8::from_str_radix(b, 16).unwrap())
            .collect();
        assert_eq!(bytes, linked.rom[address..address + bytes.len()]);
    }
    assert_eq!(replies[9]["body"]["breakpoints"][0]["verified"], true);
    assert_eq!(replies[11]["reason"], "Breakpoint");
    assert_eq!(replies[11]["sp"], 0xD000);
    assert_eq!(
        replies[11]["pc"],
        linked
            .symbols
            .iter()
            .find(|s| s.name == "__koh_exit")
            .unwrap()
            .value
    );
    let registers = replies[12]["body"]["variables"].as_array().unwrap();
    for (name, value) in [("A", "$00"), ("DE", "$0000"), ("HL", "$002A")] {
        assert!(
            registers
                .iter()
                .any(|r| r["name"] == name && r["value"] == value)
        );
    }
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn genuinely_symbol_free_roslyn_assembly_compiles_and_executes(
    host: &Host,
    #[case] configuration: &str,
) {
    let (directory, images) = gameboy_images(host, configuration, "SourceDebug");
    let reference_dir = directory.path().join("reference");
    std::fs::create_dir(&reference_dir).unwrap();
    std::fs::write(reference_dir.join("Koh.GameBoy.dll"), &images[1]).unwrap();
    let project = directory.path().join("SourceDebug.csproj");
    let text = std::fs::read_to_string(&project).unwrap();
    let reference =
        "<ProjectReference Include=\"$(KohRoot)/src/Compiler/Koh.GameBoy/Koh.GameBoy.csproj\" />";
    assert!(text.contains(reference));
    std::fs::write(&project, text.replace(reference,
        "<Reference Include=\"Koh.GameBoy\"><HintPath>reference/Koh.GameBoy.dll</HintPath></Reference>"
    )).unwrap();
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    for name in ["SourceDebug", "Koh.GameBoy"] {
        std::fs::remove_file(output.join(format!("{name}.pdb"))).unwrap();
    }
    let build = super::dotnet()
        .arg("build")
        .arg(directory.path().join("SourceDebug.csproj"))
        .args([
            "-t:Rebuild",
            "-c",
            configuration,
            "-p:DebugType=None",
            "-p:DebugSymbols=false",
            "-m:1",
            "--nologo",
            "-v:q",
        ])
        .arg(format!(
            "-p:KohRoot={}",
            Path::new(env!("CARGO_MANIFEST_DIR"))
                .parent()
                .unwrap()
                .parent()
                .unwrap()
                .display()
        ))
        .output()
        .unwrap();
    assert!(
        build.status.success(),
        "{}{}",
        String::from_utf8_lossy(&build.stdout),
        String::from_utf8_lossy(&build.stderr)
    );
    assert!(!output.join("SourceDebug.pdb").exists());
    assert!(!output.join("Koh.GameBoy.pdb").exists());
    assert_eq!(
        std::fs::read(output.join("Koh.GameBoy.dll")).unwrap(),
        images[1]
    );
    for name in ["SourceDebug"] {
        let image = std::fs::read(output.join(format!("{name}.dll"))).unwrap();
        let pe = object::read::pe::PeFile32::parse(image.as_slice()).unwrap();
        if let Some(directory) = pe.data_directory(object::pe::IMAGE_DIRECTORY_ENTRY_DEBUG) {
            let bytes = directory
                .data(image.as_slice(), &pe.section_table())
                .unwrap();
            assert_eq!(bytes.len() % 28, 0);
            for entry in bytes.chunks_exact(28) {
                let kind = u32::from_le_bytes(entry[12..16].try_into().unwrap());
                assert!(
                    ![2, 17, 19].contains(&kind),
                    "CodeView/embedded PDB/checksum remains"
                );
            }
        }
    }
    let target = directory.path().join("without symbols.o");
    let result = Command::new(env!("CARGO_BIN_EXE_koh-compile"))
        .arg(output.join("SourceDebug.dll"))
        .arg("-r")
        .arg(output.join("Koh.GameBoy.dll"))
        .arg("-o")
        .arg(&target)
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    let object = koh_objects::read_elf(&std::fs::read(target).unwrap()).unwrap();
    assert!(object.sections.iter().all(|s| s.lines.is_empty()));
    assert!(
        object
            .symbols
            .iter()
            .any(|s| s.name.contains("Program::Main"))
    );
    let linked = koh_link::link(&[object], &Default::default()).unwrap();
    assert_eq!(
        super::execute(host, directory.path(), &linked, "__koh_exit"),
        "00 0000002A D000 OK"
    );
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn cli_discovers_optional_symbols_and_rejects_bad_or_aliased_inputs(
    host: &Host,
    #[case] configuration: &str,
) {
    let (directory, _) = gameboy_images(host, configuration, "SourceDebug");
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let assembly = output.join("SourceDebug.dll");
    let reference = output.join("Koh.GameBoy.dll");
    let pdb = assembly.with_extension("pdb");
    let saved = std::fs::read(&pdb).unwrap();
    let explicit = directory.path().join("explicit source symbols.pdb");
    std::fs::write(&explicit, &saved).unwrap();
    let target = directory.path().join("source object.o");
    let run = |flags: &[OsString], target: &Path| {
        Command::new(env!("CARGO_BIN_EXE_koh-compile"))
            .arg(&assembly)
            .arg("-r")
            .arg(&reference)
            .arg("-o")
            .arg(target)
            .args(flags)
            .output()
            .unwrap()
    };
    let inspect = || koh_objects::read_elf(&std::fs::read(&target).unwrap()).unwrap();
    let result = run(&[], &target);
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert!(
        inspect()
            .sections
            .iter()
            .flat_map(|s| &s.lines)
            .any(|line| line.file.ends_with("mapped source.cs"))
    );
    let result = run(&["--pdb".into(), explicit.as_os_str().into()], &target);
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    let result = run(&["--pdb".into(), explicit.as_os_str().into()], &explicit);
    assert!(!result.status.success());
    assert_eq!(std::fs::read(&explicit).unwrap(), saved);
    let result = run(
        &[
            "--no-pdb".into(),
            "--pdb".into(),
            explicit.as_os_str().into(),
        ],
        &target,
    );
    assert!(!result.status.success());
    std::fs::write(&pdb, b"invalid symbols").unwrap();
    let before = std::fs::read(&target).unwrap();
    let result = run(&[], &target);
    assert!(!result.status.success());
    assert!(String::from_utf8_lossy(&result.stderr).contains("Portable PDB"));
    assert_eq!(std::fs::read(&target).unwrap(), before);
    let result = run(&["--no-pdb".into()], &target);
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert!(inspect().sections.iter().all(|s| s.lines.is_empty()));
    std::fs::remove_file(&pdb).unwrap();
    std::fs::remove_file(reference.with_extension("pdb")).unwrap();
    let result = run(&[], &target);
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert!(inspect().sections.iter().all(|s| s.lines.is_empty()));
    let result = run(&["--pdb".into(), explicit.as_os_str().into()], &target);
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert!(inspect().sections.iter().any(|s| !s.lines.is_empty()));
}
