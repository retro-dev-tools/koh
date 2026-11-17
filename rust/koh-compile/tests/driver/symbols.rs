use super::{Host, gameboy_images, host};
use rstest::rstest;
use std::{ffi::OsString, path::Path, process::Command};

#[rstest::fixture]
#[once]
fn debugger() -> Host {
    super::support::build_fixture_host("DebuggerHost")
}

fn source_document<'a>(lines: &'a [koh_link::LinkedLine], source: &Path, line: usize) -> &'a Path {
    let physical = source.canonicalize().unwrap();
    let document = lines
        .iter()
        .find(|point| {
            point.line as usize == line
                && Path::new(&point.file).canonicalize().ok().as_ref() == Some(&physical)
        })
        .unwrap_or_else(|| {
            panic!(
                "Missing source document {}:{line}; emitted lines: {lines:?}",
                source.display()
            )
        });
    Path::new(&document.file)
}

fn debugger_replies(debugger: &Host, requests: &[String]) -> Vec<serde_json::Value> {
    use std::{io::Write, process::Stdio};
    let mut child = super::dotnet()
        .arg(&debugger.dll)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let mut input = child.stdin.take().unwrap();
    let text = requests.join("\n");
    let writer = std::thread::spawn(move || input.write_all(text.as_bytes()));
    let output = child.wait_with_output().unwrap();
    let written = writer.join().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    written.unwrap();
    let replies: Vec<serde_json::Value> = String::from_utf8(output.stdout)
        .unwrap()
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect();
    assert_eq!(replies.len(), requests.len(), "{replies:#?}");
    replies
}

#[rstest]
#[case("Debug", false, false)]
#[case("Release", false, false)]
#[case("Debug", true, true)]
#[case("Release", true, true)]
fn debugger_stops_on_native_csharp_source_and_inspects_machine_state(
    host: &Host,
    debugger: &Host,
    #[case] configuration: &str,
    #[case] recursive: bool,
    #[case] color: bool,
) {
    use koh_compile::{compile, resolve::Resolver};
    use serde_json::json;

    #[cfg(unix)]
    let alias_root = tempfile::tempdir_in(host.directory.path()).unwrap();
    #[cfg(unix)]
    let alias_host = {
        let alias = alias_root.path().join("source alias");
        std::os::unix::fs::symlink(host.directory.path(), &alias).unwrap();
        Host {
            directory: tempfile::tempdir_in(alias).unwrap(),
            dll: host.dll.clone(),
        }
    };
    #[cfg(unix)]
    let host = &alias_host;
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
    let entry = if recursive {
        "Program::InspectRecursive"
    } else {
        "Program::Inspect"
    };
    let mut object = compile::object_with_debug(&resolver, Some(entry), &symbol_views).unwrap();
    let mut bank = 1;
    for section in &mut object.sections {
        if section.name.starts_with(".text.__koh_a") {
            section.bank = Some(bank);
            section.address = Some(0x4000);
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
    let source = source_document(&linked.lines, &source, line);
    let breakpoint = linked
        .lines
        .iter()
        .find(|p| p.file == source.to_str().unwrap() && p.line == line as u32)
        .unwrap_or_else(|| {
            panic!(
                "Missing source breakpoint {}:{line}; emitted lines: {:?}",
                source.display(),
                linked.lines
            )
        });
    let rom = directory.path().join("debug game.gb");
    let kdbg = directory.path().join("debug game.kdbg");
    std::fs::write(&rom, &linked.rom).unwrap();
    std::fs::write(&kdbg, linked.kdbg().unwrap()).unwrap();
    let mut requests = Vec::new();
    for (command, arguments) in [
        (
            "launch",
            json!({"program": rom, "debugInfo": kdbg, "hardwareMode": if color { "cgb" } else { "dmg" }}),
        ),
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
        ("variables", json!({"variablesReference": 4})),
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
    let replies = debugger_replies(debugger, &requests);
    for (index, reply) in replies.iter().enumerate() {
        if index != 3 && index != 12 {
            assert_eq!(reply["success"], true, "{reply}");
        }
    }
    assert_eq!(replies[1]["body"]["breakpoints"][0]["verified"], true);
    assert_eq!(replies[1]["body"]["breakpoints"][0]["line"], line);
    assert_eq!(replies[3]["reason"], "Breakpoint");
    assert_eq!(replies[3]["bootMapped"], false);
    assert_eq!(replies[3]["pc"], breakpoint.address);
    assert_eq!(replies[3]["bank"], breakpoint.bank);
    assert!(replies[3]["sp"].as_u64().unwrap() < 0xD000);
    assert_eq!(replies[3]["stack"].as_str().unwrap().len(), 128);
    let frame = &replies[4]["body"]["stackFrames"][0];
    assert_eq!(frame["source"]["path"], source.to_str().unwrap());
    assert_eq!(frame["line"], line);
    let source_text = std::fs::read_to_string(&source).unwrap();
    let caller_line = source_text
        .lines()
        .position(|line| {
            line.contains(if recursive {
                "return Probe(value + 1);"
            } else {
                "public static int Inspect()"
            })
        })
        .unwrap()
        + 1;
    let caller = &replies[4]["body"]["stackFrames"][1];
    assert_eq!(caller["source"]["path"], source.to_str().unwrap());
    assert_eq!(caller["line"], caller_line);
    if recursive {
        let frames = replies[4]["body"]["stackFrames"].as_array().unwrap();
        assert_eq!(frames[2]["source"]["path"], source.to_str().unwrap());
        assert_eq!(frames[2]["line"], caller_line);
        let root_line = source_text
            .lines()
            .position(|line| line.contains("public static int InspectRecursive()"))
            .unwrap()
            + 1;
        assert_eq!(frames[3]["source"]["path"], source.to_str().unwrap());
        assert_eq!(frames[3]["line"], root_line);
    }
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
        let offset = if address < 0x4000 {
            address
        } else {
            breakpoint.bank as usize * 0x4000 + address - 0x4000
        };
        assert_eq!(bytes, linked.rom[offset..offset + bytes.len()]);
    }
    for reply in [&replies[7], &replies[9]] {
        assert!(
            !reply["body"]["variables"]
                .as_array()
                .unwrap()
                .iter()
                .any(|s| s["name"]
                    .as_str()
                    .is_some_and(|s| s.contains("__koh_unwind_"))
                    || s["value"]
                        .as_str()
                        .is_some_and(|s| s.contains("__koh_unwind_")))
        );
    }
    assert_eq!(replies[10]["body"]["breakpoints"][0]["verified"], true);
    assert_eq!(replies[12]["reason"], "Breakpoint");
    assert_eq!(replies[12]["sp"], 0xD000);
    assert_eq!(
        replies[12]["pc"],
        linked
            .symbols
            .iter()
            .find(|s| s.name == "__koh_exit")
            .unwrap()
            .value
    );
    let registers = replies[13]["body"]["variables"].as_array().unwrap();
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
fn native_stack_recipes_reject_unknown_or_corrupt_states(host: &Host, debugger: &Host) {
    use koh_compile::{compile, resolve::Resolver};
    use serde_json::json;
    let (directory, images) = gameboy_images(host, "Release", "SourceDebug");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let object = compile::object(&resolver, None).unwrap();
    let linked = koh_link::link(&[object], &Default::default()).unwrap();
    let stub = linked
        .symbols
        .iter()
        .find(|s| s.name == "__koh_far_return")
        .unwrap()
        .value as u16;
    let rom = directory.path().join("unwind.gb");
    let kdbg = directory.path().join("unwind.kdbg");
    std::fs::write(&rom, &linked.rom).unwrap();
    std::fs::write(&kdbg, linked.kdbg().unwrap()).unwrap();
    let cases = [
        ("banked", "b_0020", 0xC800u16, stub, 0x7FF0u16, 1u16, 2),
        ("unsupported_near", "n_0020", 0xC800, 0x7FF0, 0x7FF0, 1, 1),
        ("missing", "missing", 0xC800, stub, 0x7FF0, 1, 1),
        ("future", "future", 0xC800, stub, 0x7FF0, 1, 1),
        ("kind", "x_0020", 0xC800, stub, 0x7FF0, 1, 1),
        ("hex", "b_xxxx", 0xC800, stub, 0x7FF0, 1, 1),
        ("large", "b_2000", 0xC800, stub, 0x7FF0, 1, 1),
        ("truncated", "truncated", 0xC800, stub, 0x7FF0, 1, 1),
        ("conflicting", "conflict", 0xC800, stub, 0x7FF0, 1, 1),
        ("duplicate", "duplicate", 0xC800, stub, 0x7FF0, 1, 2),
        ("below_stack", "b_0020", 0xC000, stub, 0x7FF0, 1, 1),
        ("past_stack", "b_0020", 0xCFFE, stub, 0x7FF0, 1, 1),
        ("wrong_stub", "b_0020", 0xC800, stub ^ 1, 0x7FF0, 1, 1),
        ("non_rom", "b_0020", 0xC800, stub, 0x8000, 1, 1),
        ("zero_pc", "b_0020", 0xC800, stub, 0, 1, 1),
        ("wide_bank", "b_0020", 0xC800, stub, 0x7FF0, 257, 1),
        ("absent_bank", "b_0020", 0xC800, stub, 0x7FF0, 2, 1),
        ("frame_limit", "limit", 0xC800, stub, 0x7FF0, 0, 32),
    ];
    assert_eq!(linked.rom.len(), 0x8000);
    let mut requests = vec![json!({"seq":1,"type":"request","command":"launch","arguments":{"program":rom,"debugInfo":kdbg}}).to_string()];
    for (index, (label, recipe, sp, return_stub, return_pc, bank, _)) in cases.iter().enumerate() {
        let pc = 0x3000 + index as u16;
        let marker = |state: &str, suffix: &str| format!("__koh_unwind_v1_{state}_{label}{suffix}");
        let symbols = match *recipe {
            "missing" => vec![],
            "future" => vec![format!("__koh_unwind_v2_b_0020_{label}")],
            "truncated" => vec!["__koh_unwind_v1_b_0020".into()],
            "conflict" => vec![marker("b_0020", "a"), marker("b_0022", "b")],
            "duplicate" => vec![marker("b_0020", "a"), marker("b_0020", "b")],
            "limit" => vec![marker("b_0000", "")],
            state => vec![marker(state, "")],
        };
        let mut writes = Vec::new();
        let count = if *recipe == "limit" { 40 } else { 1 };
        for frame in 0..count {
            let entry = if *recipe == "limit" {
                *sp + frame * 6
            } else {
                *sp + 0x20
            };
            let caller_pc = if *recipe == "limit" { pc } else { *return_pc };
            for (offset, value) in [(0, *return_stub), (2, caller_pc), (4, *bank)] {
                for (byte, value) in value.to_le_bytes().into_iter().enumerate() {
                    writes.push(json!([entry + offset + byte as u16, value]));
                }
            }
        }
        requests.push(json!({"command":"state","arguments":{"pc":pc,"sp":sp,"symbols":symbols,"writes":writes}}).to_string());
        requests.push(json!({"seq":requests.len()+1,"type":"request","command":"stackTrace","arguments":{"threadId":1}}).to_string());
    }
    let replies = debugger_replies(debugger, &requests);
    assert_eq!(replies[0]["success"], true);
    for (index, (label, _, _, _, pc, _, expected)) in cases.iter().enumerate() {
        assert_eq!(replies[1 + index * 2]["configured"], true, "{label}");
        let reply = &replies[2 + index * 2];
        assert_eq!(reply["success"], true, "{label}: {reply}");
        let frames = reply["body"]["stackFrames"].as_array().unwrap();
        assert_eq!(frames.len(), *expected, "{label}: {reply}");
        if *expected == 2 {
            assert_eq!(
                frames[1]["instructionPointerReference"],
                format!("0x{pc:04X}"),
                "{label}"
            );
        }
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", true)]
fn sdk_2048_source_debugger_recovers_framework_callers(
    debugger: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    use serde_json::json;
    let directory = tempfile::Builder::new()
        .prefix("koh SDK source debugging ")
        .tempdir()
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let project = directory.path().join("Game.csproj");
    std::fs::write(
        &project,
        r#"<Project>
<Import Project="$(KohRoot)src/Compiler/Koh.Sdk/Sdk/Sdk.props" />
<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
<ItemGroup><Compile Include="$(KohRoot)samples/csharp/gb-2048-v2/*.cs" /></ItemGroup>
<Import Project="$(KohRoot)src/Compiler/Koh.Sdk/Sdk/Sdk.targets" />
</Project>"#,
    )
    .unwrap();
    let rom = directory.path().join("game.gb");
    let object = directory.path().join("game.o");
    let kdbg = rom.with_extension("kdbg");
    let built = super::dotnet()
        .arg("build")
        .arg(&project)
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .arg(format!("-p:KohRoot={}/", root.display()))
        .arg(format!("-p:KohRomPath={}", rom.display()))
        .arg(format!("-p:KohObjectPath={}", object.display()))
        .arg(format!("-p:KohCgbCompatible={color}"))
        .arg(format!(
            "-p:KohCompilerExecutable={}",
            env!("CARGO_BIN_EXE_koh-compile")
        ))
        .env("CARGO_BUILD_JOBS", "1")
        .output()
        .unwrap();
    assert!(
        built.status.success(),
        "{}{}",
        String::from_utf8_lossy(&built.stdout),
        String::from_utf8_lossy(&built.stderr)
    );
    let object = koh_objects::read_elf(&std::fs::read(object).unwrap()).unwrap();
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
    assert_eq!(std::fs::read(&rom).unwrap(), linked.rom);
    assert_eq!(std::fs::read(&kdbg).unwrap(), linked.kdbg().unwrap());
    let source = root.join("samples/csharp/gb-2048-v2/Scenes.cs");
    let line = std::fs::read_to_string(&source)
        .unwrap()
        .lines()
        .position(|s| s.trim() == "Assets.Load();")
        .unwrap()
        + 1;
    let framework = root.join("src/Compiler/Koh.GameBoy/Framework/Game.cs");
    let framework_line = std::fs::read_to_string(&framework)
        .unwrap()
        .lines()
        .position(|s| s.trim() == "first.Enter();")
        .unwrap()
        + 1;
    let main = root.join("samples/csharp/gb-2048-v2/Program.cs");
    let main_line = std::fs::read_to_string(&main)
        .unwrap()
        .lines()
        .position(|s| s.contains("static void Main()"))
        .unwrap()
        + 1;
    let source = source_document(&linked.lines, &source, line);
    let framework = source_document(&linked.lines, &framework, framework_line);
    let main = source_document(&linked.lines, &main, main_line);
    let update = linked
        .symbols
        .iter()
        .find(|s| {
            s.name
                .starts_with("Koh.Samples.Gb2048V2.TitleScene::Update [")
        })
        .unwrap();
    let mut requests = Vec::new();
    for (command, arguments) in [
        (
            "launch",
            json!({"program":rom,"debugInfo":kdbg,"hardwareMode":if color {"cgb"} else {"dmg"}}),
        ),
        (
            "setBreakpoints",
            json!({"source":{"path":source},"breakpoints":[{"line":line}]}),
        ),
        ("continue", json!({"threadId":1})),
        ("advance", json!({"frames":600})),
        ("stackTrace", json!({"threadId":1})),
        (
            "setFunctionBreakpoints",
            json!({"breakpoints":[{"name":update.name}]}),
        ),
        ("continue", json!({"threadId":1})),
        ("advance", json!({"frames":600})),
    ] {
        requests.push(json!({"seq":requests.len()+1,"type":"request","command":command,"arguments":arguments}).to_string());
    }
    let replies = debugger_replies(debugger, &requests);
    for index in [0, 1, 2, 4, 5, 6] {
        assert_eq!(replies[index]["success"], true, "{}", replies[index]);
    }
    assert_eq!(replies[1]["body"]["breakpoints"][0]["verified"], true);
    assert_eq!(replies[1]["body"]["breakpoints"][0]["line"], line);
    assert_eq!(replies[3]["reason"], "Breakpoint");
    assert_eq!(replies[3]["bootMapped"], false);
    let frames = replies[4]["body"]["stackFrames"].as_array().unwrap();
    assert_eq!(frames[0]["source"]["path"], source.to_str().unwrap());
    assert_eq!(frames[0]["line"], line);
    let mapped_callers: Vec<_> = frames
        .iter()
        .skip(1)
        .filter_map(|f| Some((f["source"]["path"].as_str()?, f["line"].as_u64()?)))
        .collect();
    assert_eq!(
        mapped_callers,
        [
            (framework.to_str().unwrap(), framework_line as u64),
            (main.to_str().unwrap(), main_line as u64),
        ],
        "{frames:#?}"
    );
    assert_eq!(replies[5]["body"]["breakpoints"][0]["verified"], true);
    assert_eq!(replies[7]["reason"], "Breakpoint");
    assert_eq!(replies[7]["pc"], update.value);
    assert_eq!(replies[7]["bank"], update.bank.unwrap());
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
