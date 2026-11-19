use super::{Host, execute_mode, gameboy_images, host};
use koh_compile::{
    cil::{Operand, decode},
    compile,
    resolve::Resolver,
};
use rstest::rstest;

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
fn constrained_own_methods_require_the_exact_closed_receiver(
    host: &Host,
    managed: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "ConstrainedOwners");
    let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
    let image = resolver.assembly(0).unwrap();
    let method = |name| {
        (1..=image.metadata().method_defs.len())
            .map(|i| 0x06000000 | i as u32)
            .find(|&token| image.method_name(token).unwrap() == name)
            .unwrap()
    };
    let body = image.method_body(method("Program::Via")).unwrap();
    let call = decode(body.il)
        .unwrap()
        .into_iter()
        .find(|i| i.opcode == 0x6f)
        .unwrap();
    assert!(matches!(call.operand, Operand::Token(_)));
    let offset = body.il.as_ptr() as usize - images[0].as_ptr() as usize + call.offset as usize + 1;
    let own = ["Program::DirectByte", "Program::DirectWide"].map(|name| {
        let body = image.method_body(method(name)).unwrap();
        let Operand::Token(token) = decode(body.il)
            .unwrap()
            .into_iter()
            .find(|i| i.opcode == 0x28)
            .unwrap()
            .operand
        else {
            panic!("Direct method token")
        };
        assert_eq!(token >> 24, 0x0a);
        assert_eq!(
            image
                .method_name(resolver.resolve_method(0, token).unwrap().definition.token)
                .unwrap(),
            "Cell`1::Read"
        );
        token
    });
    for (receiver, entry) in ["Program::Byte", "Program::Wide"].into_iter().enumerate() {
        for target in [None, Some(0usize), Some(1usize)] {
            let mut bytes = images[0].clone();
            if let Some(target) = target {
                bytes[offset..offset + 4].copy_from_slice(&own[target].to_le_bytes());
            }
            let changed = Resolver::new(&[&bytes, &images[1]]).unwrap();
            let result = compile::object(&changed, Some(entry));
            if target.is_some_and(|target| target != receiver) {
                let error = result.err().expect(
                    "Constrained receiver and method owner must have identical type arguments",
                );
                assert!(
                    error.contains("Constrained value method owner") && error.contains("IL_"),
                    "{error}"
                );
                continue;
            }
            let object = result.unwrap_or_else(|e| panic!("{entry} target={target:?}: {e}"));
            let assembly = directory.path().join("matching own method.dll");
            std::fs::write(&assembly, &bytes).unwrap();
            let desktop = managed
                .command()
                .arg(assembly)
                .arg(entry.trim_start_matches("Program::"))
                .output()
                .unwrap();
            assert!(desktop.status.success());
            assert_eq!(
                String::from_utf8_lossy(&desktop.stdout).trim(),
                "42",
                "{entry} target={target:?}: {}",
                String::from_utf8_lossy(&desktop.stderr)
            );
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
                "{entry} target={target:?}"
            );
            super::assert_heap_snapshot(fields[4], &[0x5a; 4096], entry);
            if color {
                super::assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
            }
        }
    }
}
