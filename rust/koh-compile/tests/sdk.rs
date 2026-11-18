#[path = "sdk/float32.rs"]
mod float32;
#[path = "sdk/game.rs"]
mod game;
#[path = "sdk/iterators.rs"]
mod iterators;
#[path = "sdk/jrpg.rs"]
mod jrpg;
#[path = "sdk/original_2048.rs"]
mod original_2048;
mod support;

use rstest::rstest;
use std::{path::Path, process::Output};

fn build(project: &Path, configuration: &str, properties: &[String]) -> Output {
    support::dotnet()
        .arg("build")
        .arg(project)
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .args(properties)
        .env("CARGO_BUILD_JOBS", "1")
        .output()
        .unwrap()
}

fn report(result: &Output) -> String {
    format!(
        "{}{}",
        String::from_utf8_lossy(&result.stdout),
        String::from_utf8_lossy(&result.stderr)
    )
}

#[test]
fn sdk_dotnet_run_launches_the_graphics_rom_headlessly() {
    let directory = tempfile::Builder::new()
        .prefix("koh SDK graphics ")
        .tempdir()
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let project = directory.path().join("Graphics.csproj");
    std::fs::write(
        &project,
        r#"<Project>
<Import Project="$(KohRoot)src/Compiler/Koh.Sdk/Sdk/Sdk.props" />
<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
<ItemGroup><Compile Include="$(KohRoot)samples/csharp/gb-gfx-demo/Game.cs" /></ItemGroup>
<Import Project="$(KohRoot)src/Compiler/Koh.Sdk/Sdk/Sdk.targets" />
</Project>"#,
    )
    .unwrap();
    let rom = directory.path().join("graphics output.gb");
    let properties = vec![
        format!("-p:KohRoot={}/", root.display()),
        "-p:KohRomPath=graphics output.gb".into(),
        "-p:KohCgbCompatible=true".into(),
    ];
    let built = build(&project, "Release", &properties);
    assert!(built.status.success(), "{}", report(&built));
    assert!(std::fs::read(&rom).unwrap().len() > 0x8000);
    let png = directory.path().join("game capture.png");
    let violations = directory.path().join("mode3 report.txt");
    let result = support::dotnet()
        .arg("run")
        .arg("--project")
        .arg(&project)
        .args(["-c", "Release", "--no-build"])
        .args(&properties)
        .args(["--", "--headless", "--frames=1200"])
        .arg(format!("--screenshot={}", png.display()))
        .arg(format!("--mode3-report={}", violations.display()))
        .env("CARGO_BUILD_JOBS", "1")
        .output()
        .unwrap();
    assert!(result.status.success(), "{}", report(&result));
    let image = std::fs::read(png).unwrap();
    assert!(image.len() > 24);
    assert_eq!(&image[..8], b"\x89PNG\r\n\x1a\n");
    assert_eq!(&image[12..16], b"IHDR");
    assert_eq!(u32::from_be_bytes(image[16..20].try_into().unwrap()), 160);
    assert_eq!(u32::from_be_bytes(image[20..24].try_into().unwrap()), 144);
    assert!(
        std::fs::read_to_string(violations)
            .unwrap()
            .contains("Mode-3 write violations: 0 total")
    );
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn sdk_uses_native_compiler_and_tracks_outputs(#[case] configuration: &str) {
    let directory = tempfile::Builder::new()
        .prefix("koh native SDK ")
        .tempdir()
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let game = directory.path().join("Game with spaces");
    let library = directory.path().join("Library");
    std::fs::create_dir(&game).unwrap();
    std::fs::create_dir(&library).unwrap();
    std::fs::create_dir(game.join("art")).unwrap();
    std::fs::write(
        game.join("art/sheet.png"),
        include_bytes!("fixtures/TileSheets/four-colors.png"),
    )
    .unwrap();
    std::fs::write(library.join("Library.csproj"), r#"<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"#).unwrap();
    std::fs::write(
        library.join("Answer.cs"),
        "public static class Answer { public static int Get() => 42; }",
    )
    .unwrap();
    let project = game.join("Game.csproj");
    std::fs::write(
        &project,
        r#"<Project>
<Import Project="$(KohRoot)src/Compiler/Koh.Sdk/Sdk/Sdk.props" />
<PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>Some.Ns</RootNamespace></PropertyGroup>
<ItemGroup><ProjectReference Include="../Library/Library.csproj" /></ItemGroup>
<Import Project="$(KohRoot)src/Compiler/Koh.Sdk/Sdk/Sdk.targets" />
</Project>"#,
    )
    .unwrap();
    std::fs::write(
        game.join("Program.cs"),
        r#"public static class Program {
static bool Palette(ushort light, ushort dark) => light == 0x7fff && dark == 0x1084;
public static int Main() {
    Hardware.BGP = 0xe4;
    int sum = 0;
    for (int i = 0; i < Some.Ns.Art.SheetTiles.Length; i++)
        sum += Some.Ns.Art.SheetTiles[i] * (i + 1);
    return sum == 16320 && Palette(Some.Ns.Art.SheetColor0, Some.Ns.Art.SheetColor3)
        && Some.Ns.Art.SheetTileCount == 1 ? Answer.Get() : -1;
}}
"#,
    )
    .unwrap();
    let rom = directory.path().join("game output.gb");
    let object = directory.path().join("canonical game.o");
    let missing = directory.path().join("missing compiler");
    let mut properties = vec![
        format!("-p:KohRoot={}/", root.display()),
        format!("-p:KohRomPath={}", rom.display()),
        format!("-p:KohObjectPath={}", object.display()),
        format!("-p:KohCompilerExecutable={}", missing.display()),
    ];
    std::fs::write(&rom, b"previous ROM").unwrap();
    let failed = build(&project, configuration, &properties);
    assert!(
        !failed.status.success(),
        "missing native compiler must fail: {}",
        report(&failed)
    );
    assert!(
        report(&failed).contains("missing compiler"),
        "{}",
        report(&failed)
    );
    assert_eq!(std::fs::read(&rom).unwrap(), b"previous ROM");
    let tool = directory
        .path()
        .join(format!("native compiler{}", std::env::consts::EXE_SUFFIX));
    std::fs::copy(env!("CARGO_BIN_EXE_koh-compile"), &tool).unwrap();
    properties[3] = format!("-p:KohCompilerExecutable={}", tool.display());
    let result = build(&project, configuration, &properties);
    assert!(result.status.success(), "{}", report(&result));
    let elf = koh_objects::read_elf(&std::fs::read(&object).unwrap()).unwrap();
    assert!(
        elf.sections
            .iter()
            .flat_map(|s| &s.lines)
            .any(|line| line.file.ends_with("Program.cs")),
        "SDK source mappings"
    );
    assert!(
        elf.sections
            .iter()
            .flat_map(|s| &s.lines)
            .any(|line| line.file.ends_with("Answer.cs")),
        "project-reference source mappings"
    );
    let linked = koh_link::link(&[elf], &Default::default()).unwrap();
    assert_eq!(std::fs::read(&rom).unwrap(), linked.rom);
    assert!(rom.with_extension("kdbg").is_file());
    let host = support::build_host();
    let exit = linked
        .symbols
        .iter()
        .find(|symbol| symbol.name == "__koh_exit")
        .unwrap();
    let executed = support::dotnet()
        .current_dir(host.directory.path())
        .arg(&host.dll)
        .args([
            "--native-video",
            &format!("{:x}:{:x}", exit.bank.unwrap_or(0), exit.value),
        ])
        .arg(&rom)
        .output()
        .unwrap();
    assert!(executed.status.success(), "{}", report(&executed));
    let output = String::from_utf8(executed.stdout).unwrap();
    let fields: Vec<_> = output.split_whitespace().collect();
    assert_eq!(fields.len(), 9, "{output}");
    assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"]);
    let planes = [
        0xffu8, 0xff, 0, 0, 0xff, 0, 0, 0xff, 0xff, 0xff, 0, 0, 0xff, 0, 0, 0xff,
    ];
    let expected: String = [16, 0]
        .into_iter()
        .chain(planes)
        .map(|byte| format!("{byte:02X}"))
        .collect();
    assert!(
        fields[4].starts_with(&expected),
        "exact tile bytes must reach WRAM"
    );
    assert_eq!(&fields[8][2..4], "E4");
    let modified = std::fs::metadata(&rom).unwrap().modified().unwrap();
    let result = build(&project, configuration, &properties);
    assert!(result.status.success(), "{}", report(&result));
    assert_eq!(
        std::fs::metadata(&rom).unwrap().modified().unwrap(),
        modified
    );
    let symbols = game.join(format!("obj/{configuration}/net10.0/Game.pdb"));
    std::fs::OpenOptions::new()
        .write(true)
        .open(&symbols)
        .unwrap()
        .set_modified(modified + std::time::Duration::from_secs(1))
        .unwrap();
    let result = build(&project, configuration, &properties);
    assert!(result.status.success(), "{}", report(&result));
    let copied = game.join(format!("bin/{configuration}/net10.0/Game.pdb"));
    assert_eq!(
        std::fs::metadata(&copied).unwrap().modified().unwrap(),
        std::fs::metadata(&symbols).unwrap().modified().unwrap(),
        "managed SDK copies the symbol input timestamp"
    );
    assert_ne!(
        std::fs::metadata(&rom).unwrap().modified().unwrap(),
        modified,
        "PDB changes invalidate ROM metadata"
    );
    let previous = std::fs::read(&rom).unwrap();
    properties.push("-p:KohEmitDebugInfo=false".into());
    let result = build(&project, configuration, &properties);
    assert!(result.status.success(), "{}", report(&result));
    assert_eq!(std::fs::read(&rom).unwrap(), previous);
    assert!(
        !rom.with_extension("kdbg").exists(),
        "disabled debug must not leave stale symbols"
    );
    let no_debug = koh_objects::read_elf(&std::fs::read(&object).unwrap()).unwrap();
    assert!(
        no_debug.sections.iter().all(|s| s.lines.is_empty()),
        "disabled PDB discovery"
    );
    properties.pop();
    let result = build(&project, configuration, &properties);
    assert!(result.status.success(), "{}", report(&result));
    assert!(rom.with_extension("kdbg").is_file());
    let modified = std::fs::metadata(&rom).unwrap().modified().unwrap();
    std::fs::OpenOptions::new()
        .write(true)
        .open(&tool)
        .unwrap()
        .set_modified(modified + std::time::Duration::from_secs(1))
        .unwrap();
    let result = build(&project, configuration, &properties);
    assert!(result.status.success(), "{}", report(&result));
    assert_ne!(
        std::fs::metadata(&rom).unwrap().modified().unwrap(),
        modified
    );
    let modified = std::fs::metadata(&rom).unwrap().modified().unwrap();
    std::fs::OpenOptions::new()
        .write(true)
        .open(&tool)
        .unwrap()
        .set_modified(modified - std::time::Duration::from_secs(1))
        .unwrap();
    let linker = directory
        .path()
        .join(format!("native linker{}", std::env::consts::EXE_SUFFIX));
    let source = root.join(format!(
        "src/Compiler/Koh.Build.Tasks/bin/{configuration}/net10.0/koh-link{}",
        std::env::consts::EXE_SUFFIX
    ));
    std::fs::copy(source, &linker).unwrap();
    std::fs::OpenOptions::new()
        .write(true)
        .open(&linker)
        .unwrap()
        .set_modified(modified - std::time::Duration::from_secs(1))
        .unwrap();
    properties.push(format!("-p:KohLinkExecutable={}", linker.display()));
    let result = build(&project, configuration, &properties);
    assert!(result.status.success(), "{}", report(&result));
    assert_ne!(
        std::fs::metadata(&rom).unwrap().modified().unwrap(),
        modified
    );
    let modified = std::fs::metadata(&rom).unwrap().modified().unwrap();
    std::fs::OpenOptions::new()
        .write(true)
        .open(&linker)
        .unwrap()
        .set_modified(modified + std::time::Duration::from_secs(1))
        .unwrap();
    let result = build(&project, configuration, &properties);
    assert!(result.status.success(), "{}", report(&result));
    assert_ne!(
        std::fs::metadata(&rom).unwrap().modified().unwrap(),
        modified
    );
    properties.push("-p:KohCgbCompatible=true".into());
    let result = build(&project, configuration, &properties);
    assert!(result.status.success(), "{}", report(&result));
    assert_eq!(std::fs::read(&rom).unwrap()[0x143], 0x80);
    properties.push("-p:KohCgbOnly=true".into());
    let result = build(&project, configuration, &properties);
    assert!(result.status.success(), "{}", report(&result));
    assert_eq!(std::fs::read(&rom).unwrap()[0x143], 0xc0);
    let previous = std::fs::read(&rom).unwrap();
    let missing_linker = directory.path().join("missing linker");
    properties.push(format!("-p:KohLinkExecutable={}", missing_linker.display()));
    let result = build(&project, configuration, &properties);
    assert!(!result.status.success(), "{}", report(&result));
    assert!(
        report(&result).contains("missing linker"),
        "{}",
        report(&result)
    );
    assert_eq!(std::fs::read(&rom).unwrap(), previous);
    properties.pop();
    properties.push(format!("-p:KohLinkExecutable={}", tool.display()));
    let result = build(&project, configuration, &properties);
    assert!(!result.status.success(), "{}", report(&result));
    assert!(report(&result).contains("exited 1"), "{}", report(&result));
    assert_eq!(std::fs::read(&rom).unwrap(), previous);
    properties.pop();
    let no_debug = directory.path().join("no debug.gb");
    properties[1] = format!("-p:KohRomPath={}", no_debug.display());
    properties.push("-p:KohEmitDebugInfo=false".into());
    let result = build(&project, configuration, &properties);
    assert!(result.status.success(), "{}", report(&result));
    assert!(no_debug.is_file());
    assert!(!no_debug.with_extension("kdbg").exists());
    std::fs::write(
        library.join("Answer.cs"),
        "public static class Answer { public static int Get() => 43; }",
    )
    .unwrap();
    let result = build(&project, configuration, &properties);
    assert!(result.status.success(), "{}", report(&result));
    let object = koh_objects::read_elf(&std::fs::read(&object).unwrap()).unwrap();
    let linked = koh_link::link(
        &[object],
        &koh_link::LinkOptions {
            cgb: koh_link::CgbMode::Only,
            ..Default::default()
        },
    )
    .unwrap();
    assert_eq!(std::fs::read(&no_debug).unwrap(), linked.rom);
    let exit = linked
        .symbols
        .iter()
        .find(|symbol| symbol.name == "__koh_exit")
        .unwrap();
    let executed = support::dotnet()
        .arg(&host.dll)
        .args([
            "--native",
            &format!("{:x}:{:x}", exit.bank.unwrap_or(0), exit.value),
        ])
        .arg(&no_debug)
        .output()
        .unwrap();
    assert!(executed.status.success(), "{}", report(&executed));
    assert_eq!(
        String::from_utf8(executed.stdout).unwrap().trim(),
        "00 0000002B D000 OK"
    );
    let assembly = game.join(format!("bin/{configuration}/net10.0/Game.dll"));
    let input = std::fs::read(&assembly).unwrap();
    properties[1] = format!("-p:KohRomPath={}", assembly.display());
    let failed = build(&project, configuration, &properties);
    assert!(!failed.status.success(), "{}", report(&failed));
    assert!(report(&failed).contains("distinct"), "{}", report(&failed));
    assert_eq!(std::fs::read(&assembly).unwrap(), input);
    #[cfg(unix)]
    {
        let alias = directory.path().join("assembly alias");
        std::os::unix::fs::symlink(assembly.parent().unwrap(), &alias).unwrap();
        properties[1] = format!("-p:KohRomPath={}", alias.join("Game.dll").display());
        let failed = build(&project, configuration, &properties);
        assert!(
            std::fs::read(&assembly).unwrap() == input,
            "output alias must preserve the managed input"
        );
        assert!(!failed.status.success(), "{}", report(&failed));
        assert!(report(&failed).contains("distinct"), "{}", report(&failed));
    }
    properties[1] = format!("-p:KohRomPath={}", no_debug.display());
    let previous = std::fs::read(&no_debug).unwrap();
    std::fs::write(
        game.join("Program.cs"),
        "public static class Program { public static int Main() => throw new System.Exception(); }",
    )
    .unwrap();
    let failed = build(&project, configuration, &properties);
    assert!(!failed.status.success(), "{}", report(&failed));
    assert!(report(&failed).contains("exited 1"), "{}", report(&failed));
    assert_eq!(std::fs::read(&no_debug).unwrap(), previous);
    std::fs::write(
        game.join("art/invalid.png"),
        include_bytes!("fixtures/TileSheets/five-colors.png"),
    )
    .unwrap();
    let failed = build(&project, configuration, &properties);
    assert!(!failed.status.success(), "{}", report(&failed));
    assert!(
        report(&failed).contains("5 distinct colors"),
        "{}",
        report(&failed)
    );
    assert_eq!(std::fs::read(&no_debug).unwrap(), previous);
}
