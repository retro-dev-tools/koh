use super::{Host, dotnet, execute_mode, gameboy_images, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;
#[path = "../support/metadata.rs"]
mod metadata;

#[rstest]
#[case("Debug", "IProducer`1")]
#[case("Release", "IProducer`1")]
#[case("Debug", "IConsumer`1")]
#[case("Release", "IConsumer`1")]
fn variance_is_unsupported_but_contradictory_flags_are_invalid(
    host: &Host,
    #[case] configuration: &str,
    #[case] name: &str,
) {
    let (directory, images) = gameboy_images(host, configuration, "Variance");
    let desktop = dotnet()
        .arg(
            directory
                .path()
                .join(format!("bin/{configuration}/net10.0/Variance.dll")),
        )
        .output()
        .unwrap();
    assert!(
        desktop.status.success(),
        "{}",
        String::from_utf8_lossy(&desktop.stderr)
    );
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let image = resolver.assembly(0).unwrap();
    let data = image.metadata();
    let owner = data
        .type_defs
        .iter()
        .position(|r| data.strings.get(r.type_name).unwrap() == name)
        .unwrap() as u32
        + 1;
    let parameter = data
        .generic_params
        .iter()
        .position(|r| r.owner.table == Some(clrmeta::TableId::TypeDef) && r.owner.row == owner)
        .unwrap();
    let entry = format!("Program::{}Shape", name.trim_end_matches("`1"));
    let error = compile::object(&resolver, Some(&entry)).unwrap_err();
    assert!(error.contains("variance-aware binding"), "{name}: {error}");
    let reference_variance = data.generic_params[parameter].flags | 4;
    for flags in [0u16, reference_variance, 3, 0x0c, 0x0d, 0x0e, 0x40, 0x41] {
        let mut changed = images[0].clone();
        let (offset, _) = metadata::table_row(&changed, clrmeta::TableId::GenericParam, parameter);
        changed[offset + 2..offset + 4].copy_from_slice(&flags.to_le_bytes());
        let resolver = Resolver::new(&[&changed, &images[1]]).unwrap();
        let result = compile::object(&resolver, Some(&entry));
        if flags == 0 {
            let object = result.unwrap();
            let linked = koh_link::link(&[object], &Default::default()).unwrap();
            let report = execute_mode(
                host,
                directory.path(),
                &linked,
                "__koh_exit",
                "--native-heap",
            );
            let fields: Vec<_> = report.split_whitespace().collect();
            assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"]);
            super::assert_heap_snapshot(fields[4], &[0x5a; 4096], &entry);
        } else {
            let error = result.unwrap_err();
            let expected = if flags == reference_variance {
                "variance-aware binding"
            } else {
                "Invalid generic type parameter flags"
            };
            assert!(
                error.contains(expected) && error.contains("IL_"),
                "{name} {flags}: {error}"
            );
        }
    }
    let error = compile::object(&resolver, Some("Program::Boxed")).unwrap_err();
    assert!(
        error.contains("opcode 0x008c")
            && error.contains("Program::Boxed")
            && error.contains("IL_"),
        "{error}"
    );
}
