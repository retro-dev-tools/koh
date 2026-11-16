use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;
#[path = "../support/metadata.rs"]
mod metadata;

#[rstest]
#[case(8, false)]
#[case(1024, true)]
fn interface_graph_work_is_bounded(host: &Host, #[case] count: usize, #[case] limited: bool) {
    let directory = tempfile::tempdir().unwrap();
    let project = directory.path().join("Work.csproj");
    std::fs::write(&project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><TreatWarningsAsErrors>true</TreatWarningsAsErrors><NuGetAudit>true</NuGetAudit></PropertyGroup></Project>").unwrap();
    let mut source = String::from("interface IRoot {int Value()=>1;}");
    for index in 0..count {
        source.push_str(&format!(
            "interface I{index}:IRoot {{int IRoot.Value()=>2;}}"
        ));
    }
    source.push_str("interface ISelected:");
    source.push_str(
        &(0..count)
            .map(|i| format!("I{i}"))
            .collect::<Vec<_>>()
            .join(","),
    );
    source.push_str("{int IRoot.Value()=>42;} class Receiver:ISelected{} public static class Program {public static int Main(){IRoot value=new Receiver(); return value.Value();}}");
    std::fs::write(directory.path().join("Program.cs"), source).unwrap();
    let result = super::dotnet()
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
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let resolver = Resolver::new(&[&image])?;
        compile::object(&resolver, None)
    }))
    .expect("Interface work limit must not panic");
    if limited {
        let error = result.unwrap_err();
        assert!(
            error.contains("Class layout dependency budget exceeded")
                && error.contains("Program::Main IL_"),
            "{error}"
        );
    } else {
        let linked = koh_link::link(&[result.unwrap()], &Default::default()).unwrap();
        assert_eq!(
            execute_mode(host, directory.path(), &linked, "__koh_exit", "--native"),
            "00 0000002A D000 OK"
        );
    }
}

#[rstest::fixture]
#[once]
fn managed() -> Host {
    super::support::build_fixture_host("ManagedHost")
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn most_specific_default_interface_bodies_execute(
    host: &Host,
    managed: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "DefaultInterfaces");
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let library = std::fs::read(output.join("InterfaceLibrary.dll")).unwrap();
    let resolver = Resolver::new(&[&images[0], &images[1], &library]).unwrap();
    let mut errors = Vec::new();
    for entry in [
        "Main",
        "Left",
        "Right",
        "Diamond",
        "Reverse",
        "Shared",
        "Inherited",
        "Hidden",
        "Rebound",
        "ClassWins",
        "Reabstract",
        "InheritedClassWins",
        "NestedCall",
        "PrivateHelper",
        "Property",
        "Initializer",
        "DeadDefault",
        "Wide",
        "ByReference",
        "Aggregate",
        "BoundDelegate",
        "Referenced",
    ] {
        let managed = super::dotnet()
            .arg(&managed.dll)
            .arg(output.join("DefaultInterfaces.dll"))
            .arg(entry)
            .output()
            .unwrap();
        assert!(
            managed.status.success(),
            "{entry}: {}",
            String::from_utf8_lossy(&managed.stderr)
        );
        assert_eq!(
            String::from_utf8_lossy(&managed.stdout).trim(),
            "42",
            "Managed {entry}: {}",
            String::from_utf8_lossy(&managed.stderr)
        );
        let mut object = match compile::object(&resolver, Some(&format!("Program::{entry}"))) {
            Ok(object) => object,
            Err(error) => {
                errors.push(format!("{entry}: {error}"));
                continue;
            }
        };
        let mut bank = 1;
        for section in &mut object.sections {
            if section.name.starts_with(".text.__koh_a") {
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
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        if color {
            assert_eq!(fields[5], "6B".repeat(4096), "{entry} bank2");
        }
    }
    assert!(errors.is_empty(), "{}", errors.join("\n"));
    let managed_result = super::dotnet()
        .arg(&managed.dll)
        .arg(output.join("DefaultInterfaces.dll"))
        .arg("NullReceiver")
        .output()
        .unwrap();
    assert!(managed_result.status.success());
    assert_eq!(
        String::from_utf8_lossy(&managed_result.stdout).trim(),
        "THROW:System.NullReferenceException"
    );
    let object = compile::object(&resolver, Some("Program::NullReceiver")).unwrap();
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
    assert_eq!(fields[0], "04");
    assert_eq!(fields[3], "OK");
    if color {
        assert_eq!(fields[5], "6B".repeat(4096));
    }
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn default_interface_ambiguities_reabstraction_and_invalid_bodies_diagnose(
    host: &Host,
    managed: &Host,
    #[case] configuration: &str,
) {
    use clrmeta::TableId;
    use koh_compile::assembly::Assembly;
    let (directory, images) = gameboy_images(host, configuration, "DefaultInterfaces");
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let library = std::fs::read(output.join("InterfaceLibrary.dll")).unwrap();
    let image = Assembly::parse(&images[0]).unwrap();
    let md = image.metadata();
    let method = |name: &str| {
        (0..md.method_defs.len())
            .find(|&i| image.method_name(0x06000001 + i as u32).unwrap() == name)
            .unwrap()
    };
    let mut probes = Vec::new();
    for (owner, entry, needle, exception) in [
        (
            "ClassWins",
            "ClassWins",
            "Ambiguous default",
            "AmbiguousImplementationException",
        ),
        (
            "Reabstract",
            "Reabstract",
            "Re-abstracted",
            "EntryPointNotFoundException",
        ),
    ] {
        let index = method(&format!("{owner}::Value"));
        let (at, _) = metadata::table_row(&images[0], TableId::MethodDef, index);
        let mut bytes = images[0].clone();
        let flags = (md.method_defs[index].flags & !0x167) | 1;
        bytes[at + 6..at + 8].copy_from_slice(&flags.to_le_bytes());
        let path = output.join(format!("{owner}Invalid.dll"));
        std::fs::write(&path, &bytes).unwrap();
        let result = super::dotnet()
            .arg(&managed.dll)
            .arg(&path)
            .arg(entry)
            .output()
            .unwrap();
        assert!(
            result.status.success(),
            "{}",
            String::from_utf8_lossy(&result.stderr)
        );
        let text = String::from_utf8_lossy(&result.stdout);
        assert!(
            text.trim().ends_with(exception)
                || (owner == "Reabstract" && text.trim() == "THROW:System.TypeLoadException"),
            "{owner}: {text}: {}",
            String::from_utf8_lossy(&result.stderr)
        );
        probes.push((owner, bytes, entry, needle));
    }
    let body = method("ILeft::IBase.Value");
    let (at, _) = metadata::table_row(&images[0], TableId::MethodDef, body);
    let flags = md.method_defs[body].flags;
    for (label, changed, rva, needle) in [
        (
            "nonfinal",
            flags & !0x20,
            md.method_defs[body].rva,
            "must be final",
        ),
        (
            "newslot",
            flags | 0x100,
            md.method_defs[body].rva,
            "without newslot",
        ),
        ("missing body", flags, 0, "executable IL body"),
        (
            "abstract executable body",
            flags | 0x400,
            md.method_defs[body].rva,
            "cannot contain executable IL",
        ),
    ] {
        let mut bytes = images[0].clone();
        bytes[at..at + 4].copy_from_slice(&rva.to_le_bytes());
        bytes[at + 6..at + 8].copy_from_slice(&changed.to_le_bytes());
        probes.push((label, bytes, "Left", needle));
    }
    for (label, bytes, entry, needle) in probes {
        let resolver = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
        let error = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap_err();
        assert!(error.contains(needle), "{label}: {error}");
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn final_interface_body_tokens_call_and_capture_exact_implementation(
    host: &Host,
    managed: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    use koh_compile::{
        assembly::Assembly,
        cil::{Operand, decode},
    };
    let (directory, images) = gameboy_images(host, configuration, "DefaultInterfaces");
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    let library = std::fs::read(output.join("InterfaceLibrary.dll")).unwrap();
    let assembly = Assembly::parse(&images[0]).unwrap();
    let method = |name: &str| {
        (0..assembly.metadata().method_defs.len())
            .map(|i| 0x06000001 + i as u32)
            .find(|&token| assembly.method_name(token).unwrap() == name)
            .unwrap()
    };
    let selected = method("ILeft::IBase.Value");
    let original = method("IBase::Value");
    let mut errors = Vec::new();
    for (entry, helper, opcode) in [
        ("FinalBodyCall", "ILeft::Invoke", 0x6f),
        ("FinalBodyDelegate", "ILeft::Bind", 0xfe07),
    ] {
        let body = assembly.method_body(method(helper)).unwrap();
        let operations: Vec<_> = decode(body.il)
            .unwrap()
            .into_iter()
            .filter(|i| i.opcode == opcode && i.operand == Operand::Token(original))
            .collect();
        assert_eq!(operations.len(), 1);
        let base = body.il.as_ptr() as usize - images[0].as_ptr() as usize;
        let offset = base + operations[0].offset as usize + if opcode > 0xff { 2 } else { 1 };
        let mut bytes = images[0].clone();
        bytes[offset..offset + 4].copy_from_slice(&selected.to_le_bytes());
        let mutated = Assembly::parse(&bytes).unwrap();
        assert!(
            decode(mutated.method_body(method(helper)).unwrap().il)
                .unwrap()
                .iter()
                .any(|i| i.opcode == opcode && i.operand == Operand::Token(selected))
        );
        let path = output.join(format!("{entry}.dll"));
        std::fs::write(&path, &bytes).unwrap();
        let result = super::dotnet()
            .arg(&managed.dll)
            .arg(&path)
            .arg(entry)
            .output()
            .unwrap();
        assert!(
            result.status.success(),
            "{}",
            String::from_utf8_lossy(&result.stderr)
        );
        assert_eq!(
            String::from_utf8_lossy(&result.stdout).trim(),
            "42",
            "{entry}: {}",
            String::from_utf8_lossy(&result.stderr)
        );
        let resolver = Resolver::new(&[&bytes, &images[1], &library]).unwrap();
        let mut object = match compile::object(&resolver, Some(&format!("Program::{entry}"))) {
            Ok(object) => object,
            Err(error) => {
                errors.push(format!("{entry}: {error}"));
                continue;
            }
        };
        let mut bank = 1;
        for section in &mut object.sections {
            if section.name.starts_with(".text.__koh_a") {
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
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        if color {
            assert_eq!(fields[5], "6B".repeat(4096), "{entry} bank2");
        }
        let (row, _) = metadata::table_row(
            &bytes,
            clrmeta::TableId::MethodDef,
            (selected & 0xffffff) as usize - 1,
        );
        let mut abstract_body = bytes.clone();
        abstract_body[row..row + 4].copy_from_slice(&0u32.to_le_bytes());
        let flags = assembly.method(selected).unwrap().flags | 0x400;
        abstract_body[row + 6..row + 8].copy_from_slice(&flags.to_le_bytes());
        let resolver = Resolver::new(&[&abstract_body, &images[1], &library]).unwrap();
        let error = compile::object(&resolver, Some(&format!("Program::{entry}"))).unwrap_err();
        assert!(
            error.contains("Abstract interface implementation body cannot be invoked directly"),
            "{entry}: {error}"
        );
    }
    assert!(errors.is_empty(), "{}", errors.join("\n"));
}
