use super::*;

pub(super) fn call(
    resolver: &crate::resolve::Resolver<'_>,
    target: Definition,
    constructing: bool,
    b: &mut Builder,
) -> Result<bool, String> {
    let Some(instance) = resolver.instance(target)? else {
        return Ok(false);
    };
    let Some(owner) = instance.declaring else {
        return Ok(false);
    };
    if !resolver.readonly_byte_span(owner)? {
        return Ok(false);
    }
    let image = resolver.assembly(instance.method.assembly)?;
    let name = image
        .metadata()
        .strings
        .get(image.method(instance.method.token)?.name)
        .map_err(|e| e.to_string())?;
    let signature = image.method_signature(instance.method.token)?;
    let layout = crate::layout::value(resolver, owner)?;
    let shape = value_type(&layout)?;
    if signature.generic_param_count != 0 {
        return Err("Readonly byte-span members cannot have method arguments".into());
    }
    if name == "get_Empty"
        && !constructing
        && !signature.calling_convention.has_this()
        && signature.params.is_empty()
    {
        let types = resolver
            .type_instance(owner)?
            .ok_or("Missing readonly byte-span arguments")?
            .arguments;
        let result = resolver.bind_signature(
            instance.method.assembly,
            &signature.return_type,
            &[],
            &types,
        )?;
        if bound_type(resolver, result.assembly, &result.ty)? != Type::Struct(shape) {
            return Err("Readonly byte-span Empty return type mismatch".into());
        }
        let value = b.value(Type::Struct(shape), Op::ZeroStruct)?;
        b.stack.push(value);
        return Ok(true);
    }
    if !signature.calling_convention.has_this() {
        return Err(format!("Unsupported readonly byte-span member: {name}"));
    }
    if name == ".ctor" {
        use clrmeta::{ElementType, TypeSig};
        if signature.params
            != [
                TypeSig::Ptr(Box::new(TypeSig::Primitive(ElementType::Void))),
                TypeSig::Primitive(ElementType::I4),
            ]
            || signature.return_type != TypeSig::Primitive(ElementType::Void)
        {
            return Err(
                "Readonly byte-span construction requires literal pointer and length".into(),
            );
        }
        let length = b.pop()?;
        if length.1 != Type::I32 {
            return Err("Readonly byte-span length requires int32".into());
        }
        let bytes = b.pop()?;
        let Type::FieldHandle(field) = bytes.1 else {
            return Err("Readonly byte-span requires an RVA literal".into());
        };
        let bytes = resolver.assembly(field.assembly)?.field_data(field.token)?;
        if bytes.is_empty() || bytes.len() > 0x4000 {
            return Err("Readonly byte-span extent exceeds its literal data".into());
        }
        b.data_bytes = b
            .data_bytes
            .checked_add(bytes.len())
            .filter(|&total| total <= 65536)
            .ok_or("Method literal data exceeds 64 KiB budget")?;
        let value = b.value(
            Type::Struct(shape),
            Op::ByteSpanLiteral {
                bytes: bytes.to_vec(),
                length: length.0,
            },
        )?;
        if constructing {
            b.stack.push(value);
        } else {
            let receiver = b.pop()?;
            if receiver.1 != Type::StructRef(shape) {
                return Err(
                    "Readonly byte-span constructor requires its exact local storage".into(),
                );
            }
            b.effect(Op::StoreMemory {
                address: receiver.0,
                value: value.0,
                bits: 48,
            });
        }
    } else {
        let mut returned = &signature.return_type;
        while let clrmeta::TypeSig::Modified {
            required,
            modifier,
            inner,
        } = returned
        {
            if *required {
                let identity = resolver.signature_type(instance.method.assembly, *modifier)?;
                if !matches!(
                    resolver.framework_definition(identity)?,
                    Some(("System.Runtime.InteropServices", "InAttribute"))
                        | Some(("System.Runtime.CompilerServices", "IsReadOnlyAttribute"))
                ) {
                    return Err("Unsupported required readonly byte-span return modifier".into());
                }
            }
            returned = inner;
        }
        let index = if name == "get_Item"
            && signature.params == [clrmeta::TypeSig::Primitive(clrmeta::ElementType::I4)]
            && *returned == clrmeta::TypeSig::ByRef(Box::new(clrmeta::TypeSig::Var(0)))
        {
            Some(b.pop()?)
        } else if name == "get_Length"
            && signature.params.is_empty()
            && signature.return_type == clrmeta::TypeSig::Primitive(clrmeta::ElementType::I4)
        {
            None
        } else {
            return Err(format!("Unsupported readonly byte-span member: {name}"));
        };
        let receiver = b.pop()?;
        if receiver.1 != Type::StructRef(shape) {
            return Err("Readonly byte-span receiver requires its exact closed type".into());
        }
        let span = b.value(
            Type::Struct(shape),
            Op::LoadMemory {
                address: receiver.0,
                bits: 48,
                signed: false,
            },
        )?;
        let value = if let Some(index) = index {
            if index.1 != Type::I32 {
                return Err("Readonly byte-span index requires int32".into());
            }
            b.value(
                Type::RomByteRef,
                Op::ByteSpanElement {
                    span: span.0,
                    index: index.0,
                },
            )?
        } else {
            b.value(
                Type::I32,
                Op::ExtractField {
                    value: span.0,
                    offset: 2,
                    storage: FieldStorage::Value(Storage::Scalar(Scalar::I32)),
                },
            )?
        };
        b.stack.push(value);
    }
    Ok(true)
}
