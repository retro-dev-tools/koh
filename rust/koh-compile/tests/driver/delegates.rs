use super::{Host, compile, execute_mode, gameboy_images, host};
use koh_compile::resolve::Resolver;
use rstest::rstest;

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn generic_callback_storage_keeps_type_and_method_arguments(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "DelegateCalls");
    let framework = framework_image(directory.path(), configuration);
    let resolver = Resolver::new(&[&images[0], &images[1], &framework]).unwrap();
    for entry in [
        "Int",
        "Byte",
        "Signed",
        "Long",
        "Mixed",
        "Array",
        "Captured",
        "OwnerGroup",
    ] {
        let object = compile::object(&resolver, Some(&format!("GenericCallbackRules::{entry}")))
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
        let text = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = text.split_whitespace().collect();
        assert_eq!(&fields[..4], &["00", "0000002A", "D000", "OK"], "{entry}");
        let mut packet = vec![0x5a; 256];
        if matches!(entry, "Long" | "Mixed" | "Captured") {
            packet[8..16].copy_from_slice(&0x123456780000002Au64.to_le_bytes());
        }
        super::assert_heap_snapshot(&fields[4][0xf00 * 2..], &packet, entry);
    }
}

pub(super) fn replace_method_token(
    image: &koh_compile::assembly::Assembly<'_>,
    bytes: &[u8],
    method: u32,
    opcode: u16,
    replacement: u32,
) -> (Vec<u8>, u32) {
    use koh_compile::cil::{Operand, decode};
    let body = image.method_body(method).unwrap();
    let instructions = decode(body.il).unwrap();
    let instruction = instructions
        .iter()
        .find(|instruction| instruction.opcode == opcode)
        .unwrap();
    let Operand::Token(original) = instruction.operand else {
        panic!("Expected method token");
    };
    let offsets: Vec<_> = bytes
        .windows(body.il.len())
        .enumerate()
        .filter_map(|(offset, candidate)| (candidate == body.il).then_some(offset))
        .collect();
    assert_eq!(offsets.len(), 1, "Unique source method body");
    let mut modified = bytes.to_vec();
    let offset = offsets[0] + instruction.offset as usize + if opcode > 0xff { 2 } else { 1 };
    modified[offset..offset + 4].copy_from_slice(&replacement.to_le_bytes());
    (modified, original)
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn sealed_virtual_tokens_execute_the_override(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "DelegateCalls");
    let framework = framework_image(directory.path(), configuration);
    let resolver = Resolver::new(&[&images[0], &images[1], &framework]).unwrap();
    let image = resolver.assembly(0).unwrap();
    let method = |name| {
        (1..=image.metadata().method_defs.len() as u32)
            .map(|row| 0x06000000 | row)
            .find(|&token| image.method_name(token).unwrap() == name)
            .unwrap()
    };
    let generic = (0..image.metadata().method_specs.len())
        .find_map(|index| {
            let token = 0x2b000001 + index as u32;
            let target = resolver.resolve_method(0, token).unwrap();
            (target.definition.token == method("Counter::Identity")).then_some(token)
        })
        .unwrap();
    for (entry, opcode, target, expected) in [
        (
            "Program::SealedCall",
            0x6f,
            method("DoubleCounter::Add"),
            "0000002A",
        ),
        (
            "Program::SealedGroup",
            0xfe07,
            method("DoubleCounter::Add"),
            "0000002A",
        ),
        (
            "Program::SealedGroup",
            0xfe07,
            method("Counter::PlainAdd"),
            "00000015",
        ),
        ("Program::SealedGroup", 0xfe07, generic, "00000015"),
    ] {
        let (modified, original) =
            replace_method_token(image, &images[0], method(entry), opcode, target);
        assert!(matches!(
            image.method_name(original).unwrap().as_str(),
            "Counter::Add" | "DoubleCounter::Add"
        ));
        let resolver = Resolver::new(&[&modified, &images[1], &framework]).unwrap();
        let object = compile::object(&resolver, Some(entry)).unwrap();
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
        let result = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = result.split_whitespace().collect();
        assert_eq!(
            &fields[..4],
            &["00", expected, "D000", "OK"],
            "{entry}: {result}"
        );
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
}

pub(super) fn framework_image(directory: &std::path::Path, configuration: &str) -> Vec<u8> {
    let references = std::fs::read_to_string(
        directory.join(format!("obj/{configuration}/net10.0/reference-paths.txt")),
    )
    .unwrap();
    std::fs::read(
        references
            .lines()
            .find(|path| {
                std::path::Path::new(path)
                    .file_name()
                    .is_some_and(|name| name == "System.Runtime.dll")
            })
            .unwrap(),
    )
    .unwrap()
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn null_delegates_fault_at_binding_or_invocation(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
) {
    let (directory, images) = gameboy_images(host, configuration, "DelegateCalls");
    let framework = framework_image(directory.path(), configuration);
    let resolver = Resolver::new(&[&images[0], &images[1], &framework]).unwrap();
    for (entry, nonvirtual) in [
        ("Program::NullInvoke", false),
        ("Program::NullBinding", false),
        ("Program::NullNonVirtualBinding", false),
        ("Program::NullBinding", true),
    ] {
        let bytes = if nonvirtual {
            let image = resolver.assembly(0).unwrap();
            let method = |name| {
                (1..=image.metadata().method_defs.len() as u32)
                    .map(|row| 0x06000000 | row)
                    .find(|&token| image.method_name(token).unwrap() == name)
                    .unwrap()
            };
            replace_method_token(
                image,
                &images[0],
                method(entry),
                0xfe07,
                method("Counter::PlainAdd"),
            )
            .0
        } else {
            images[0].clone()
        };
        let resolver = Resolver::new(&[&bytes, &images[1], &framework]).unwrap();
        let object = compile::object(&resolver, Some(entry)).unwrap();
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
        let result = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_abort",
            "--native-heap",
        );
        let fields: Vec<_> = result.split_whitespace().collect();
        assert_eq!(fields[0], "04", "{entry}: {result}");
        assert_eq!(fields[3], "OK", "{entry}: frame guards");
        super::assert_heap_snapshot(
            fields[4],
            &[0x5a; 4096],
            "Null checks precede callable allocation",
        );
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], entry);
        }
    }
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn delegate_targets_require_nominal_parameter_types(host: &Host, #[case] configuration: &str) {
    let (directory, images) = gameboy_images(host, configuration, "DelegateCalls");
    let framework = framework_image(directory.path(), configuration);
    let resolver = Resolver::new(&[&images[0], &images[1], &framework]).unwrap();
    let image = resolver.assembly(0).unwrap();
    let method = |name| {
        (1..=image.metadata().method_defs.len() as u32)
            .map(|row| 0x06000000 | row)
            .find(|&token| image.method_name(token).unwrap() == name)
            .unwrap()
    };
    let replacement = method("Program::AcceptInt");
    for (entry, original) in [
        ("Program::ByteTarget", "Program::AcceptByte"),
        ("Program::EnumTarget", "Program::AcceptEnum"),
    ] {
        let (forged, target) =
            replace_method_token(image, &images[0], method(entry), 0xfe06, replacement);
        assert_eq!(target, method(original));
        let resolver = Resolver::new(&[&forged, &images[1], &framework]).unwrap();
        let error = compile::object(&resolver, Some(entry)).unwrap_err();
        assert!(
            error.contains("Delegate target parameter type differs from Invoke")
                && error.contains("IL_"),
            "{entry}: {error}"
        );
    }
}

#[rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn delegates_preserve_stored_targets_captures_and_virtual_binding(
    host: &Host,
    #[case] configuration: &str,
    #[case] color: bool,
    #[values(
        "StaticTarget",
        "StaticLambdas",
        "CapturedTarget",
        "InstanceTarget",
        "WideTarget",
        "FunctionTargets",
        "ConstructorFields",
        "ReturnedAndArray",
        "ByteTarget",
        "EnumTarget",
        "NullDelegateShapes"
    )]
    name: &str,
) {
    let (directory, images) = gameboy_images(host, configuration, "DelegateCalls");
    let desktop = super::dotnet()
        .arg(
            directory
                .path()
                .join(format!("bin/{configuration}/net10.0/DelegateCalls.dll")),
        )
        .output()
        .unwrap();
    assert_eq!(
        desktop.status.code(),
        Some(42),
        "{}",
        String::from_utf8_lossy(&desktop.stderr)
    );
    let framework = framework_image(directory.path(), configuration);
    let resolver = Resolver::new(&[&images[0], &images[1], &framework]).unwrap();
    {
        let entry = if name == "NullDelegateShapes" {
            "NullDelegateShapes::Main".into()
        } else {
            format!("Program::{name}")
        };
        if name == "NullDelegateShapes" {
            use koh_compile::ir::{ReferenceCell, Type};
            let functions =
                compile::reachable(&resolver, compile::entry(&resolver, Some(&entry)).unwrap())
                    .unwrap();
            let lookup = |name| {
                functions
                    .iter()
                    .find(|function| {
                        resolver
                            .assembly(function.identity.assembly)
                            .unwrap()
                            .method_name(function.identity.token)
                            .unwrap()
                            == name
                    })
                    .unwrap()
                    .returns
            };
            let Some(Type::Delegate(narrow)) = lookup("NullDelegateShapes::ByteFunction") else {
                panic!("Missing narrow delegate return");
            };
            let Some(Type::ReferenceCell(ReferenceCell::Delegate(wide))) =
                lookup("NullDelegateShapes::Cell")
            else {
                panic!("Missing wide delegate cell");
            };
            assert_eq!(narrow.identity(), wide.identity());
            assert_ne!(
                narrow, wide,
                "Generic delegate identity preserves byte versus int arguments"
            );
            let first = lookup("NullDelegateShapes::FirstFunction");
            let second = lookup("NullDelegateShapes::SecondFunction");
            let integer = lookup("NullDelegateShapes::IntegerFunction");
            assert_ne!(
                first, second,
                "Distinct enum arguments retain nominal identity"
            );
            assert_ne!(
                first, integer,
                "Enum identity is distinct from its underlying int storage"
            );
            assert_ne!(second, integer);
        }
        let mut object = compile::object(&resolver, Some(&entry))
            .unwrap_or_else(|error| panic!("{entry}: {error}"));
        let first_bank = if (configuration == "Debug") == color {
            1
        } else {
            257
        };
        let mut next_bank = first_bank;
        for section in &mut object.sections {
            if section.name.starts_with(".text.__koh_a") {
                assert_eq!(section.kind, 1);
                section.bank = Some(next_bank);
                next_bank += 1;
            }
        }
        assert!(
            next_bank > first_bank + 1,
            "Calls must span distinct ROM banks"
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
        let result = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = result.split_whitespace().collect();
        assert_eq!(
            &fields[..4],
            &["00", "0000002A", "D000", "OK"],
            "{entry}: {result}"
        );
        if color {
            super::assert_heap_snapshot(fields[5], &[0x6b; 4096], &entry);
        }
    }
}

#[rstest]
#[case("Debug")]
#[case("Release")]
fn delegate_metadata_preserves_invoke_identity_and_instantiation(
    host: &Host,
    #[case] configuration: &str,
) {
    use clrmeta::{ElementType, TypeSig};
    let (directory, images) = gameboy_images(host, configuration, "DelegateCalls");
    let framework = framework_image(directory.path(), configuration);
    let resolver = Resolver::new(&[&images[0], &images[1], &framework]).unwrap();
    let metadata = resolver.assembly(0).unwrap().metadata();
    let reference = |name| {
        metadata
            .type_refs
            .iter()
            .position(|ty| metadata.strings.get(ty.type_name).unwrap() == name)
            .unwrap() as u32
            + 0x01000001
    };
    let definition = |name| {
        metadata
            .type_defs
            .iter()
            .position(|ty| metadata.strings.get(ty.type_name).unwrap() == name)
            .unwrap() as u32
            + 0x02000001
    };
    let action = resolver
        .delegate_type(0, reference("Action"))
        .unwrap()
        .unwrap();
    assert!(action.signature.params.is_empty());
    assert_eq!(
        action.signature.return_type,
        TypeSig::Primitive(ElementType::Void)
    );
    assert!(action.type_arguments.is_empty());
    assert_eq!(action.identity.assembly, 2);
    let wide = resolver
        .delegate_type(0, definition("WideCallback"))
        .unwrap()
        .unwrap();
    assert_eq!(
        wide.signature.return_type,
        TypeSig::Primitive(ElementType::I8)
    );
    assert_eq!(
        wide.signature.params,
        [
            TypeSig::Primitive(ElementType::I8),
            TypeSig::ByRef(Box::new(TypeSig::Primitive(ElementType::I4)))
        ]
    );
    assert_eq!(wide.identity.assembly, 0);
    assert!(
        resolver
            .delegate_type(0, definition("Counter"))
            .unwrap()
            .is_none()
    );
    assert!(
        resolver
            .delegate_type(0, reference("Func`2"))
            .unwrap_err()
            .contains("arity")
    );
    let token = (0..metadata.type_specs.len()).map(|i| i as u32 + 0x1b000001)
        .find(|&token| matches!(koh_compile::signature::type_spec(metadata.blobs.get(
            metadata.type_specs[(token & 0xffffff) as usize - 1].signature).unwrap()).unwrap(),
            TypeSig::GenericInst { type_args, .. } if type_args == [TypeSig::Primitive(ElementType::I4), TypeSig::Primitive(ElementType::I4)]))
        .unwrap();
    let function = resolver.delegate_type(0, token).unwrap().unwrap();
    assert_eq!(
        function.type_arguments,
        [
            koh_compile::resolve::TypeArgument {
                assembly: 0,
                ty: TypeSig::Primitive(ElementType::I4)
            },
            koh_compile::resolve::TypeArgument {
                assembly: 0,
                ty: TypeSig::Primitive(ElementType::I4)
            }
        ]
    );
    assert_eq!(function.signature.params, [TypeSig::Var(0)]);
    assert_eq!(function.signature.return_type, TypeSig::Var(1));
    for shape in [&action, &wide, &function] {
        assert_ne!(shape.constructor, shape.invoke);
        assert_eq!(shape.signature.calling_convention.0, 0x20);
    }
    let heap = metadata.blobs.data();
    let offsets: Vec<_> = images[0]
        .windows(heap.len())
        .enumerate()
        .filter_map(|(offset, bytes)| (bytes == heap).then_some(offset))
        .collect();
    assert_eq!(offsets.len(), 1);
    let constructor = resolver
        .assembly(0)
        .unwrap()
        .method(wide.constructor.token)
        .unwrap()
        .signature;
    let invoke = resolver
        .assembly(0)
        .unwrap()
        .method(wide.invoke.token)
        .unwrap()
        .signature;
    assert_eq!(
        metadata.blobs.get(constructor).unwrap(),
        &[0x20, 2, 1, 0x1c, 0x18]
    );
    for (name, signature, offset, value) in [
        ("static-constructor", constructor, 0, 0),
        ("constructor-result", constructor, 2, 8),
        ("constructor-target", constructor, 3, 8),
        ("constructor-pointer-width", constructor, 4, 10),
        ("static-invoke", invoke, 0, 0),
    ] {
        assert!(metadata.blobs.get(signature).unwrap().len() < 128);
        let mut image = images[0].clone();
        image[offsets[0] + signature as usize + 1 + offset] = value;
        let resolver = Resolver::new(&[&image, &images[1], &framework]).unwrap();
        let error = resolver
            .delegate_type(0, definition("WideCallback"))
            .unwrap_err();
        if name.starts_with("static-") {
            assert!(
                error.contains("WideCallback::") && error.contains("static/instance"),
                "{name}: {error}"
            );
        } else {
            assert!(
                error.contains("Invalid delegate constructor signature"),
                "{name}: {error}"
            );
        }
    }
    let runtime = metadata
        .assembly_refs
        .iter()
        .find(|reference| metadata.strings.get(reference.name).unwrap() == "System.Runtime")
        .unwrap();
    assert_eq!(
        metadata.blobs.get(runtime.public_key_or_token).unwrap(),
        &[0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a]
    );
    let mut forged = images[0].clone();
    forged[offsets[0] + runtime.public_key_or_token as usize + 1] ^= 0xff;
    let resolver = Resolver::new(&[&forged, &images[1], &framework]).unwrap();
    assert!(
        resolver
            .delegate_type(0, definition("WideCallback"))
            .unwrap()
            .is_none()
    );
}
