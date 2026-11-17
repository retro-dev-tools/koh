use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;
use std::{path::Path, process::Command};

#[rstest]
#[case("Debug")]
#[case("Release")]
fn readonly_literals_obey_the_cumulative_frontend_data_limit(#[case] configuration: &str) {
    let term = format!("sum += \"{}\"u8[0];", "K".repeat(4095));
    let method = |name, count| {
        format!(
            "public static int {name}() {{ int sum = 0; {} return sum; }}",
            term.repeat(count)
        )
    };
    let source = format!(
        "public static class Program {{ {} {} }}",
        method("AtLimit", 16),
        method("BeyondLimit", 17)
    );
    let [image, runtime] = fixture(&source, configuration);
    let resolver = Resolver::new(&[&image, &runtime]).unwrap();
    let object = compile::object(&resolver, Some("Program::AtLimit")).unwrap();
    let mut bytes = vec![b'K'; 4095];
    bytes.push(0);
    assert!(
        object
            .sections
            .iter()
            .any(|section| section.name.starts_with(".rodata.") && section.data == bytes)
    );
    let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
    koh_link::link(&[object], &Default::default()).unwrap();
    let error = compile::object(&resolver, Some("Program::BeyondLimit"))
        .err()
        .expect("More than64KiB of materialized method literals must diagnose");
    assert!(
        error.contains("literal data exceeds 64 KiB budget")
            && error.contains("BeyondLimit")
            && error.contains("IL_"),
        "{error}"
    );
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn reachable_literal_data_obeys_the_program_budget(#[case] configuration: &str) {
    let term = format!("sum += \"{}\"u8[0];", "K".repeat(4095));
    let mut source = String::from("public static class Program {");
    let literals: Vec<_> = ['A', 'B', 'C', 'D']
        .into_iter()
        .map(|c| c.to_string().repeat(8191))
        .collect();
    for index in 0..256 {
        source.push_str(&format!("static string Part{index}(int n) {{ switch(n) {{ case 0: return \"{}\"; case 1: return \"{}\"; case 2: return \"{}\"; default: return \"{}\"; }} }}", literals[0], literals[1], literals[2], literals[3]));
    }
    source.push_str(&format!(
        "static int SpanPart() {{ int sum = 0; {} return sum; }}",
        term.repeat(16)
    ));
    for name in ["AtLimit", "BeyondLimit"] {
        source.push_str(&format!("public static int {name}() {{ int sum = 0;"));
        for index in 0..256 {
            source.push_str(&format!("Part{index}(0);"));
        }
        if name == "BeyondLimit" {
            source.push_str("sum += SpanPart();");
        }
        source.push_str("return sum; }");
    }
    source.push('}');
    let [image, runtime] = fixture(&source, configuration);
    let resolver = Resolver::new(&[&image, &runtime]).unwrap();
    let entry = compile::entry(&resolver, Some("Program::AtLimit")).unwrap();
    let functions = compile::reachable(&resolver, entry).unwrap();
    assert_eq!(functions.len(), 257);
    let bytes: usize = functions
        .iter()
        .flat_map(|f| &f.blocks)
        .flat_map(|b| &b.instructions)
        .map(|i| match &i.op {
            koh_compile::ir::Op::ByteSpanLiteral { bytes, .. } => bytes.len(),
            koh_compile::ir::Op::StringLiteral(units) => units.len() * 2 + 2,
            _ => 0,
        })
        .sum();
    assert_eq!(bytes, 16 * 1024 * 1024);
    drop(functions);
    let entry = compile::entry(&resolver, Some("Program::BeyondLimit")).unwrap();
    let error = compile::reachable(&resolver, entry)
        .err()
        .expect("Reachable literals beyond16MiB must diagnose");
    assert!(
        error.contains("literal data exceeds 16 MiB budget")
            && error.contains("SpanPart")
            && error.contains("IL_"),
        "{error}"
    );
}

fn fixture(source: &str, configuration: &str) -> [Vec<u8>; 2] {
    let directory = tempfile::Builder::new()
        .prefix("koh literal limits ")
        .tempdir()
        .unwrap();
    let project = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests/fixtures/LiteralLimits/LiteralLimits.csproj");
    std::fs::copy(project, directory.path().join("LiteralLimits.csproj")).unwrap();
    std::fs::write(directory.path().join("Program.cs"), source).unwrap();
    let output = Command::new(std::env::var_os("KOH_DOTNET").unwrap_or("dotnet".into()))
        .arg("build")
        .arg(directory.path().join("LiteralLimits.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let image = std::fs::read(
        directory
            .path()
            .join(format!("bin/{configuration}/net10.0/LiteralLimits.dll")),
    )
    .unwrap();
    let references = std::fs::read_to_string(
        directory
            .path()
            .join(format!("obj/{configuration}/net10.0/reference-paths.txt")),
    )
    .unwrap();
    let runtime = references
        .lines()
        .map(Path::new)
        .find(|p| {
            p.file_name()
                .is_some_and(|name| name == "System.Runtime.dll")
        })
        .unwrap();
    let runtime = std::fs::read(runtime).unwrap();
    [image, runtime]
}
