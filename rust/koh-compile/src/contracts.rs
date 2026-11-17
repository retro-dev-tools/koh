use crate::resolve::{Definition, Resolver};
use clrmeta::{CodedIndex, CodedIndexKind, CustomAttributeRow, Metadata, TableId, TypeSig};
use std::collections::{HashMap, HashSet};

#[derive(Clone, Copy, PartialEq)]
enum Kind {
    Intrinsic,
    Interrupt,
    Aligned,
    Runtime,
}

pub(crate) enum Intrinsic {
    Allocate,
    ResetArena,
    MemoryFill,
    MemoryCopy,
    VBlankCopy,
    VramWrite,
    HBlankStream(u16),
    CpuControl(crate::ir::CpuControl),
    OamDma,
    Read(u16),
    Write(u16),
    Region(u16),
}

fn payload(bytes: &[u8]) -> Result<(&str, i32), String> {
    let read = || -> Result<(&str, i32), clrmeta::error::Error> {
        let mut reader = clrmeta::reader::Reader::new(bytes);
        if reader.read_u16()? != 1 {
            return Err(clrmeta::error::Error::ValidationError(
                "expected custom-attribute prolog 0001".into(),
            ));
        }
        let length = reader.read_compressed_uint()? as usize;
        if (reader.position() == 4 && length < 128) || reader.position() == 6 {
            return Err(clrmeta::error::Error::ValidationError(
                "noncanonical intrinsic kind length".into(),
            ));
        }
        if length > 128 {
            return Err(clrmeta::error::Error::ValidationError(
                "intrinsic kind exceeds 128 bytes".into(),
            ));
        }
        let name = reader.read_bytes(length)?;
        let address = reader.read_u32()? as i32;
        if reader.read_u16()? != 0 || !reader.is_empty() {
            return Err(clrmeta::error::Error::ValidationError(
                "unexpected named arguments/trailing bytes".into(),
            ));
        }
        let name =
            std::str::from_utf8(name).map_err(|_| clrmeta::error::Error::InvalidString(2))?;
        Ok((name, address))
    };
    read().map_err(|e| format!("Invalid Koh intrinsic attribute payload: {e}"))
}

fn row<T>(rows: &[T], index: u32) -> Result<&T, String> {
    index
        .checked_sub(1)
        .and_then(|i| rows.get(i as usize))
        .ok_or("Invalid attribute metadata row".into())
}

fn named(
    metadata: &Metadata,
    index: CodedIndex,
    depth: usize,
) -> Result<Option<(u32, &str, &str)>, String> {
    if depth > 64 {
        return Err("Attribute type nesting exceeds limit".into());
    }
    let (namespace, name) = match index.table {
        Some(TableId::TypeDef) => {
            let ty = row(&metadata.type_defs, index.row)?;
            (ty.type_namespace, ty.type_name)
        }
        Some(TableId::TypeRef) => {
            let ty = row(&metadata.type_refs, index.row)?;
            (ty.type_namespace, ty.type_name)
        }
        Some(TableId::TypeSpec) => {
            let spec = row(&metadata.type_specs, index.row)?;
            let blob = metadata
                .blobs
                .get(spec.signature)
                .map_err(|e| e.to_string())?;
            let base = match crate::signature::type_spec(blob)? {
                TypeSig::Class(base)
                | TypeSig::ValueType(base)
                | TypeSig::GenericInst { type_ref: base, .. } => base,
                _ => return Err("Attribute constructor requires a named type".into()),
            };
            return named(
                metadata,
                CodedIndex::decode(CodedIndexKind::TypeDefOrRef, base),
                depth + 1,
            );
        }
        _ => return Err("Invalid attribute declaring-type scope".into()),
    };
    let token = ((index.table.ok_or("Missing attribute type table")? as u32) << 24) | index.row;
    Ok(Some((
        token,
        metadata.strings.get(namespace).map_err(|e| e.to_string())?,
        metadata.strings.get(name).map_err(|e| e.to_string())?,
    )))
}

fn kind(
    resolver: &Resolver<'_>,
    assembly: usize,
    attribute: &CustomAttributeRow,
) -> Result<Option<Kind>, String> {
    let metadata = resolver.assembly(assembly)?.metadata();
    let constructor = attribute.attr_type;
    let (parent, name, signature) = match constructor.table {
        Some(TableId::MethodDef) => {
            let method = row(&metadata.method_defs, constructor.row)?;
            let owner = metadata
                .type_defs
                .partition_point(|ty| ty.method_list <= constructor.row);
            if owner == 0 {
                return Err("Attribute constructor has no declaring type".into());
            }
            (
                CodedIndex {
                    table: Some(TableId::TypeDef),
                    row: owner as u32,
                },
                method.name,
                method.signature,
            )
        }
        Some(TableId::MemberRef) => {
            let member = row(&metadata.member_refs, constructor.row)?;
            (member.class, member.name, member.signature)
        }
        _ => return Err("Invalid attribute constructor table".into()),
    };
    if metadata.strings.get(name).map_err(|e| e.to_string())? != ".ctor" {
        return Err("Attribute constructor must name .ctor".into());
    }
    let Some((token, namespace, name)) = named(metadata, parent, 0)? else {
        return Ok(None);
    };
    if namespace != "Koh.GameBoy" {
        return Ok(None);
    }
    let kind = match name {
        "KohIntrinsicAttribute" => Kind::Intrinsic,
        "InterruptAttribute" => Kind::Interrupt,
        "KohAlignedAttribute" => Kind::Aligned,
        "KohRuntimeAttribute" => Kind::Runtime,
        _ => return Ok(None),
    };
    let owner = resolver.resolve_type(assembly, token)?;
    if resolver.assembly(owner.assembly)?.name()? != "Koh.GameBoy" {
        return Ok(None);
    }
    let signature =
        crate::signature::method(metadata.blobs.get(signature).map_err(|e| e.to_string())?)?;
    if !signature.calling_convention.has_this()
        || signature.generic_param_count != 0
        || signature.return_type != TypeSig::Primitive(clrmeta::ElementType::Void)
    {
        return Err("Invalid attribute constructor signature".into());
    }
    let expected = match kind {
        Kind::Intrinsic => vec![
            TypeSig::Primitive(clrmeta::ElementType::String),
            TypeSig::Primitive(clrmeta::ElementType::I4),
        ],
        Kind::Interrupt | Kind::Runtime => vec![TypeSig::Primitive(clrmeta::ElementType::String)],
        Kind::Aligned => vec![TypeSig::Primitive(clrmeta::ElementType::I4)],
    };
    if signature.params != expected {
        return Err("Invalid Koh attribute constructor parameters".into());
    }
    let token = ((constructor
        .table
        .ok_or("Missing attribute constructor table")? as u32)
        << 24)
        | constructor.row;
    resolver.resolve_method(assembly, token)?;
    Ok(Some(kind))
}

pub(crate) fn assembly(resolver: &Resolver<'_>, assembly: usize) -> Result<(), String> {
    let metadata = resolver.assembly(assembly)?.metadata();
    let mut kinds = HashMap::new();
    for attribute in &metadata.custom_attributes {
        let key = (attribute.attr_type.table, attribute.attr_type.row);
        let resolved = if let Some(&kind) = kinds.get(&key) {
            kind
        } else {
            let kind = kind(resolver, assembly, attribute)?;
            kinds.insert(key, kind);
            kind
        };
        match resolved {
            Some(Kind::Runtime) => {
                runtime_declaration(resolver, assembly, attribute)?;
            }
            Some(Kind::Aligned) => {
                if attribute.parent.table != Some(TableId::Field) {
                    return Err("KohAligned must target a static field".into());
                }
                let field = row(&metadata.fields, attribute.parent.row)?;
                if field.flags & 0x10 == 0 || field.flags & 0x40 != 0 {
                    return Err("KohAligned requires a nonliteral static field".into());
                }
                alignment_payload(
                    metadata
                        .blobs
                        .get(attribute.value)
                        .map_err(|e| e.to_string())?,
                )?;
            }
            Some(Kind::Interrupt) => {
                if attribute.parent.table != Some(TableId::MethodDef) {
                    return Err("Interrupt attribute must target a method".into());
                }
                row(&metadata.method_defs, attribute.parent.row)?;
                return Err(format!(
                    "Assembly {}: Interrupt handlers require interrupt lowering",
                    resolver.assembly(assembly)?.name()?
                ));
            }
            Some(Kind::Intrinsic) => {
                match attribute.parent.table {
                    Some(TableId::MethodDef) => {
                        row(&metadata.method_defs, attribute.parent.row)?;
                    }
                    Some(TableId::Property) => {
                        row(&metadata.properties, attribute.parent.row)?;
                    }
                    _ => return Err("Koh intrinsic must target a method or property".into()),
                };
                payload(
                    metadata
                        .blobs
                        .get(attribute.value)
                        .map_err(|e| e.to_string())?,
                )
                .map_err(|error| {
                    format!(
                        "Assembly {assembly} attribute {:?} row {}: {error}",
                        attribute.parent.table, attribute.parent.row
                    )
                })?;
            }
            None => {}
        }
    }
    Ok(())
}

fn runtime_payload(bytes: &[u8]) -> Result<&str, String> {
    let read = || -> Result<&str, clrmeta::error::Error> {
        let mut reader = clrmeta::reader::Reader::new(bytes);
        if reader.read_u16()? != 1 {
            return Err(clrmeta::error::Error::ValidationError(
                "invalid runtime attribute prolog".into(),
            ));
        }
        let length = reader.read_compressed_uint()? as usize;
        if length == 0 || length > 128 || reader.position() != 3 + usize::from(length >= 128) {
            return Err(clrmeta::error::Error::ValidationError(
                "invalid runtime key length".into(),
            ));
        }
        let key = std::str::from_utf8(reader.read_bytes(length)?)
            .map_err(|_| clrmeta::error::Error::InvalidString(2))?;
        if !key
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_'))
            || reader.read_u16()? != 0
            || !reader.is_empty()
        {
            return Err(clrmeta::error::Error::ValidationError(
                "invalid runtime key or named arguments".into(),
            ));
        }
        Ok(key)
    };
    read().map_err(|error| format!("Invalid KohRuntime payload: {error}"))
}

fn runtime_declaration(
    resolver: &Resolver<'_>,
    assembly: usize,
    attribute: &CustomAttributeRow,
) -> Result<(String, Definition), String> {
    if attribute.parent.table != Some(TableId::MethodDef) {
        return Err("KohRuntime must target a static method".into());
    }
    let metadata = resolver.assembly(assembly)?.metadata();
    let method = row(&metadata.method_defs, attribute.parent.row)?;
    let signature = crate::signature::method(
        metadata
            .blobs
            .get(method.signature)
            .map_err(|e| e.to_string())?,
    )?;
    let owner = metadata
        .type_defs
        .partition_point(|ty| ty.method_list <= attribute.parent.row) as u32;
    if owner == 0 {
        return Err("KohRuntime method has no declaring type".into());
    }
    if method.flags & 0x10 == 0
        || method.flags & 0x400 != 0
        || signature.calling_convention.0 != 0
        || signature.sentinel.is_some()
        || signature.generic_param_count != 0
    {
        return Err(
            "KohRuntime requires a nongeneric static method with standard calling convention"
                .into(),
        );
    }
    resolver
        .check_arity(
            Definition {
                assembly,
                token: 0x02000000 | owner,
            },
            0,
        )
        .map_err(|error| format!("KohRuntime declaring type must be nongeneric: {error}"))?;
    let definition = Definition {
        assembly,
        token: 0x06000000 | attribute.parent.row,
    };
    let key = runtime_payload(
        metadata
            .blobs
            .get(attribute.value)
            .map_err(|e| e.to_string())?,
    )
    .map_err(|error| format!("Runtime method {definition:?}: {error}"))?;
    Ok((key.to_owned(), definition))
}

fn discover_runtime(resolver: &Resolver<'_>) -> Result<(), String> {
    if resolver.runtime_methods.borrow().is_some() {
        return Ok(());
    }
    let mut methods = std::collections::BTreeMap::new();
    for assembly in 0..resolver.assembly_count() {
        let metadata = resolver.assembly(assembly)?.metadata();
        let mut kinds = HashMap::new();
        for attribute in &metadata.custom_attributes {
            let constructor = (attribute.attr_type.table, attribute.attr_type.row);
            let resolved = if let Some(&kind) = kinds.get(&constructor) {
                kind
            } else {
                let resolved = kind(resolver, assembly, attribute)?;
                kinds.insert(constructor, resolved);
                resolved
            };
            if resolved != Some(Kind::Runtime) {
                continue;
            }
            if methods.len() >= 4096 {
                return Err("Runtime method index exceeds4096 declarations".into());
            }
            let (key, definition) = runtime_declaration(resolver, assembly, attribute)?;
            if let Some(previous) = methods.insert(key.clone(), definition) {
                return Err(format!(
                    "Duplicate KohRuntime operation key {key}: {previous:?} and {definition:?}"
                ));
            }
        }
    }
    *resolver.runtime_methods.borrow_mut() = Some(methods);
    Ok(())
}

fn runtime_method(resolver: &Resolver<'_>, key: &str) -> Result<Definition, String> {
    discover_runtime(resolver)?;
    let definition = resolver
        .runtime_methods
        .borrow()
        .as_ref()
        .and_then(|methods| methods.get(key))
        .copied()
        .ok_or_else(|| {
            format!("Missing KohRuntime {key} implementation; include a runtime assembly")
        })?;
    let metadata = resolver.assembly(definition.assembly)?.metadata();
    let method = row(&metadata.method_defs, definition.token & 0xffffff)?;
    if method.rva == 0 || method.impl_flags & 7 != 0 {
        return Err(format!(
            "KohRuntime {key} {definition:?} has no CIL body; supply its implementation assembly"
        ));
    }
    Ok(definition)
}

pub(crate) fn float_binary(resolver: &Resolver<'_>, key: &str) -> Result<Definition, String> {
    let definition = runtime_method(resolver, key)?;
    let metadata = resolver.assembly(definition.assembly)?.metadata();
    let method = row(&metadata.method_defs, definition.token & 0xffffff)?;
    let signature = crate::signature::method(
        metadata
            .blobs
            .get(method.signature)
            .map_err(|e| e.to_string())?,
    )?;
    let raw = TypeSig::Primitive(clrmeta::ElementType::U4);
    let (returns, name) = if key == "f32.cmp" {
        (TypeSig::Primitive(clrmeta::ElementType::U1), "byte")
    } else {
        (raw.clone(), "uint")
    };
    if signature.params != [raw.clone(), raw] || signature.return_type != returns {
        return Err(format!(
            "KohRuntime {key} {definition:?} requires {name}(uint,uint) raw-bit signature"
        ));
    }
    Ok(definition)
}

pub(crate) fn float_conversion(resolver: &Resolver<'_>, key: &str) -> Result<Definition, String> {
    use clrmeta::ElementType::{I4, U4};
    let (parameter, returns, expected) = match key {
        "i32.toF32" => (I4, U4, "uint(int)"),
        "u32.toF32" => (U4, U4, "uint(uint)"),
        "f32.toI32" => (U4, I4, "int(uint)"),
        "f32.toU32" => (U4, U4, "uint(uint)"),
        _ => return Err(format!("Unsupported runtime conversion {key}")),
    };
    let definition = runtime_method(resolver, key)?;
    let metadata = resolver.assembly(definition.assembly)?.metadata();
    let method = row(&metadata.method_defs, definition.token & 0xffffff)?;
    let signature = crate::signature::method(
        metadata
            .blobs
            .get(method.signature)
            .map_err(|e| e.to_string())?,
    )?;
    if signature.params != [TypeSig::Primitive(parameter)]
        || signature.return_type != TypeSig::Primitive(returns)
    {
        return Err(format!(
            "KohRuntime {key} {definition:?} requires {expected} raw-bit signature"
        ));
    }
    Ok(definition)
}

fn alignment_payload(bytes: &[u8]) -> Result<u16, String> {
    if bytes.len() != 8 || bytes[..2] != [1, 0] || bytes[6..] != [0, 0] {
        return Err("Invalid KohAligned attribute payload".into());
    }
    let value = u32::from_le_bytes(
        bytes[2..6]
            .try_into()
            .map_err(|_| "Invalid alignment integer")?,
    );
    if value == 0 || value > 4096 || !value.is_power_of_two() {
        return Err("KohAligned requires a power of two from 1 through 4096".into());
    }
    Ok(value as u16)
}

pub(crate) fn field_alignment(resolver: &Resolver<'_>, field: Definition) -> Result<u16, String> {
    let metadata = resolver.assembly(field.assembly)?.metadata();
    let mut alignment = None;
    for attribute in &metadata.custom_attributes {
        if attribute.parent.table == Some(TableId::Field)
            && attribute.parent.row == field.token & 0xffffff
            && kind(resolver, field.assembly, attribute)? == Some(Kind::Aligned)
        {
            let value = alignment_payload(
                metadata
                    .blobs
                    .get(attribute.value)
                    .map_err(|e| e.to_string())?,
            )?;
            if alignment.replace(value).is_some() {
                return Err("Multiple KohAligned attributes on one field".into());
            }
        }
    }
    Ok(alignment.unwrap_or(1))
}

pub(crate) fn intrinsic(
    resolver: &Resolver<'_>,
    method: Definition,
) -> Result<Option<Intrinsic>, String> {
    let metadata = resolver.assembly(method.assembly)?.metadata();
    let rid = method.token & 0x00ff_ffff;
    let properties: HashSet<_> = metadata
        .method_semantics
        .iter()
        .filter(|s| s.method == rid && s.association.table == Some(TableId::Property))
        .map(|s| s.association.row)
        .collect();
    let mut found = None;
    for attribute in &metadata.custom_attributes {
        let direct =
            attribute.parent.table == Some(TableId::MethodDef) && attribute.parent.row == rid;
        let property = attribute.parent.table == Some(TableId::Property)
            && properties.contains(&attribute.parent.row);
        if (direct || property)
            && kind(resolver, method.assembly, attribute)? == Some(Kind::Intrinsic)
        {
            if found.is_some() {
                return Err("Multiple Koh intrinsic attributes on a method/accessor".into());
            }
            let blob = metadata
                .blobs
                .get(attribute.value)
                .map_err(|e| e.to_string())?;
            found = Some(payload(blob)?);
        }
    }
    let Some((kind, address)) = found else {
        return Ok(None);
    };
    let signature = resolver
        .assembly(method.assembly)?
        .method_signature(method.token)?;
    let owner = metadata
        .type_defs
        .partition_point(|ty| ty.method_list <= rid) as u32;
    if signature.calling_convention.has_this()
        || signature.generic_param_count != 0
        || metadata
            .generic_params
            .iter()
            .any(|p| p.owner.table == Some(TableId::TypeDef) && p.owner.row == owner)
    {
        return Err("Koh intrinsic requires a static non-generic method".into());
    }
    if kind == "hblankstream" {
        use clrmeta::ElementType;
        if !(0..=0xffff).contains(&address)
            || !crate::ir::is_hblank_register(address as u16)
            || signature.calling_convention.0 != 0
            || signature.sentinel.is_some()
            || signature.return_type != TypeSig::Primitive(ElementType::Void)
            || signature.params
                != [
                    TypeSig::Ptr(Box::new(TypeSig::Primitive(ElementType::U1))),
                    TypeSig::Primitive(ElementType::U1),
                    TypeSig::Primitive(ElementType::U1),
                ]
        {
            return Err("Invalid Koh hblankstream intrinsic contract: expected static void(byte*, byte, byte) and a display register".into());
        }
        return Ok(Some(Intrinsic::HBlankStream(address as u16)));
    }
    if kind == "vramwrite" {
        use clrmeta::ElementType;
        if address != -1
            || signature.calling_convention.0 != 0
            || signature.sentinel.is_some()
            || signature.return_type != TypeSig::Primitive(ElementType::Void)
            || signature.params
                != [
                    TypeSig::Ptr(Box::new(TypeSig::Primitive(ElementType::U1))),
                    TypeSig::Primitive(ElementType::U1),
                ]
        {
            return Err("Invalid Koh vramwrite intrinsic contract: expected address-less static void(byte*, byte)".into());
        }
        return Ok(Some(Intrinsic::VramWrite));
    }
    if matches!(kind, "memfill" | "memcopy" | "vblankcopy") {
        use clrmeta::ElementType;
        let copied = kind != "memfill";
        let returns = if kind == "vblankcopy" {
            ElementType::U2
        } else {
            ElementType::Void
        };
        let second = if copied {
            TypeSig::Ptr(Box::new(TypeSig::Primitive(ElementType::U1)))
        } else {
            TypeSig::Primitive(ElementType::U1)
        };
        if address != -1
            || signature.calling_convention.0 != 0
            || signature.sentinel.is_some()
            || signature.return_type != TypeSig::Primitive(returns)
            || signature.params
                != [
                    TypeSig::Ptr(Box::new(TypeSig::Primitive(ElementType::U1))),
                    second,
                    TypeSig::Primitive(ElementType::U2),
                ]
        {
            let value = if copied { "byte*" } else { "byte" };
            let result = if kind == "vblankcopy" {
                "ushort"
            } else {
                "void"
            };
            return Err(format!(
                "Invalid Koh {kind} intrinsic contract: expected address-less static {result}(byte*, {value}, ushort)"
            ));
        }
        return Ok(Some(if kind == "vblankcopy" {
            Intrinsic::VBlankCopy
        } else if copied {
            Intrinsic::MemoryCopy
        } else {
            Intrinsic::MemoryFill
        }));
    }
    if matches!(kind, "alloc" | "heapreset") {
        use clrmeta::ElementType;
        let valid = if kind == "alloc" {
            signature.params == [TypeSig::Primitive(ElementType::I4)]
                && signature.return_type
                    == TypeSig::Ptr(Box::new(TypeSig::Primitive(ElementType::U1)))
        } else {
            signature.params.is_empty()
                && signature.return_type == TypeSig::Primitive(ElementType::Void)
        };
        if address != -1
            || signature.calling_convention.0 != 0
            || signature.sentinel.is_some()
            || !valid
        {
            return Err(format!(
                "Invalid Koh {kind} intrinsic contract: expected address-less static {}",
                if kind == "alloc" {
                    "byte*(int32)"
                } else {
                    "void()"
                }
            ));
        }
        return Ok(Some(if kind == "alloc" {
            Intrinsic::Allocate
        } else {
            Intrinsic::ResetArena
        }));
    }
    if matches!(kind, "di" | "ei" | "halt" | "nop" | "stop") {
        if address != -1
            || signature.calling_convention.0 != 0
            || signature.sentinel.is_some()
            || !signature.params.is_empty()
            || signature.return_type != TypeSig::Primitive(clrmeta::ElementType::Void)
        {
            return Err(format!(
                "Invalid Koh {kind} intrinsic contract: expected address-less static void()"
            ));
        }
        use crate::ir::CpuControl;
        return Ok(Some(Intrinsic::CpuControl(match kind {
            "di" => CpuControl::DisableInterrupts,
            "ei" => CpuControl::EnableInterrupts,
            "halt" => CpuControl::Halt,
            "nop" => CpuControl::Nop,
            _ => CpuControl::Stop,
        })));
    }
    if kind == "oamdma" {
        use clrmeta::ElementType;
        if address != -1
            || signature.calling_convention.0 != 0
            || signature.sentinel.is_some()
            || signature.params != [TypeSig::Primitive(ElementType::U1)]
            || signature.return_type != TypeSig::Primitive(ElementType::Void)
        {
            return Err(
                "Invalid Koh oamdma intrinsic contract: expected address-less static void(byte)"
                    .into(),
            );
        }
        return Ok(Some(Intrinsic::OamDma));
    }
    if !matches!(kind, "register" | "region") {
        return Err(format!("Unsupported Koh intrinsic '{kind}'"));
    }
    let address =
        u16::try_from(address).map_err(|_| "Koh intrinsic address must be in 0000..FFFF")?;
    use clrmeta::ElementType;
    let byte = TypeSig::Primitive(ElementType::U1);
    let void = TypeSig::Primitive(ElementType::Void);
    match kind {
        "register" if signature.params.is_empty() && signature.return_type == byte => {
            Ok(Some(Intrinsic::Read(address)))
        }
        "register" if signature.params == [byte] && signature.return_type == void => {
            Ok(Some(Intrinsic::Write(address)))
        }
        "region"
            if signature.params.is_empty()
                && matches!(
                    signature.return_type,
                    TypeSig::Ptr(_)
                        | TypeSig::Primitive(ElementType::IntPtr | ElementType::UIntPtr)
                ) =>
        {
            Ok(Some(Intrinsic::Region(address)))
        }
        "register" | "region" => Err(format!("Invalid Koh {kind} intrinsic signature")),
        _ => Err(format!("Unsupported Koh intrinsic '{kind}'")),
    }
}

pub(crate) fn method(resolver: &Resolver<'_>, method: Definition) -> Result<(), String> {
    if intrinsic(resolver, method)?.is_some() {
        return Err("Koh hardware intrinsic cannot compile its desktop body as an entry".into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::{alignment_payload, payload, runtime_payload};
    use rstest::rstest;

    #[rstest]
    #[case("f32.add")]
    #[case("a")]
    #[case(&"x".repeat(128))]
    fn runtime_key_round_trip_and_truncation(#[case] key: &str) {
        let mut bytes = vec![1, 0];
        if key.len() == 128 {
            bytes.extend([0x80, 0x80]);
        } else {
            bytes.push(key.len() as u8);
        }
        bytes.extend(key.bytes());
        bytes.extend([0, 0]);
        assert_eq!(runtime_payload(&bytes).unwrap(), key);
        for end in 0..bytes.len() {
            assert!(runtime_payload(&bytes[..end]).is_err(), "prefix {end}");
        }
    }

    #[rstest]
    #[case(b"\x02\x00\x01a\x00\x00")]
    #[case(b"\x01\x00\x00\x00\x00")]
    #[case(b"\x01\x00\xff")]
    #[case(b"\x01\x00\x01\xfe\x00\x00")]
    #[case(b"\x01\x00\x01-\x00\x00")]
    #[case(b"\x01\x00\x01a\x01\x00")]
    #[case(b"\x01\x00\x01a\x00\x00\x00")]
    #[case(b"\x01\x00\x80\x01a\x00\x00")]
    fn invalid_runtime_key_is_diagnostic(#[case] bytes: &[u8]) {
        assert!(
            runtime_payload(bytes)
                .unwrap_err()
                .contains("KohRuntime payload")
        );
    }

    const VALID: &[u8] = b"\x01\x00\x08register\x40\xff\x00\x00\x00\x00";

    #[rstest]
    #[case(1)]
    #[case(16)]
    #[case(256)]
    #[case(4096)]
    fn valid_alignment_payload(#[case] value: u32) {
        let mut bytes = vec![1, 0];
        bytes.extend(value.to_le_bytes());
        bytes.extend([0, 0]);
        assert_eq!(alignment_payload(&bytes).unwrap(), value as u16);
        for end in 0..bytes.len() {
            assert!(alignment_payload(&bytes[..end]).is_err());
        }
        for index in [0, 1, 6, 7] {
            let mut malformed = bytes.clone();
            malformed[index] ^= 1;
            assert!(alignment_payload(&malformed).is_err());
        }
        bytes.push(0);
        assert!(alignment_payload(&bytes).is_err());
    }

    #[rstest]
    #[case(0)]
    #[case(3)]
    #[case(4097)]
    #[case(8192)]
    #[case(u32::MAX)]
    fn invalid_alignment_value(#[case] value: u32) {
        let mut bytes = vec![1, 0];
        bytes.extend(value.to_le_bytes());
        bytes.extend([0, 0]);
        assert!(
            alignment_payload(&bytes)
                .unwrap_err()
                .contains("power of two")
        );
    }

    #[test]
    fn intrinsic_payload_round_trip_and_truncation() {
        assert_eq!(payload(VALID).unwrap(), ("register", 0xff40));
        for end in 0..VALID.len() {
            assert!(payload(&VALID[..end]).is_err(), "prefix {end}");
        }
    }

    #[rstest]
    #[case(b"\x02\x00\x08register\x40\xff\x00\x00\x00\x00")]
    #[case(b"\x01\x00\xff")]
    #[case(b"\x01\x00\x01\xfe\x40\xff\x00\x00\x00\x00")]
    #[case(b"\x01\x00\x08register\x40\xff\x00\x00\x01\x00")]
    #[case(b"\x01\x00\x08register\x40\xff\x00\x00\x00\x00\x00")]
    #[case(b"\x01\x00\x80\x08register\x40\xff\x00\x00\x00\x00")]
    fn invalid_intrinsic_payload_is_diagnostic(#[case] bytes: &[u8]) {
        assert!(payload(bytes).unwrap_err().contains("intrinsic attribute"));
    }
}
