use crate::{
    resolve::{Definition, Resolver, framework_type},
    signature,
};
use clrmeta::{ElementType, TableId, TypeSig};
use std::{collections::HashSet, sync::Arc};
mod classes;
pub(crate) use classes::implements;
pub use classes::{ClassLayout, class};

#[derive(Debug, Clone)]
pub struct Field {
    pub identity: Definition,
    pub offset: u16,
    pub signature: TypeSig,
    pub signature_assembly: usize,
}

#[derive(Debug, Clone)]
pub struct Layout {
    pub identity: Definition,
    pub bytes: u16,
    pub alignment: u8,
    pub fields: Vec<Field>,
    pub contains_references: bool,
    depth: u8,
}

pub fn value(resolver: &Resolver<'_>, identity: Definition) -> Result<Arc<Layout>, String> {
    Build {
        resolver,
        active: HashSet::new(),
        fields: 0,
    }
    .value(identity)
}

struct Build<'r, 'a> {
    resolver: &'r Resolver<'a>,
    active: HashSet<Definition>,
    fields: usize,
}

fn aligned(value: u32, alignment: u32) -> Result<u32, String> {
    value
        .checked_add(alignment - 1)
        .map(|value| value & !(alignment - 1))
        .ok_or_else(|| "Struct layout alignment overflow".into())
}

impl Build<'_, '_> {
    fn value(&mut self, identity: Definition) -> Result<Arc<Layout>, String> {
        let identity = self
            .resolver
            .resolve_type(identity.assembly, identity.token)?;
        if let Some(layout) = self.resolver.value_layouts.borrow().get(&identity) {
            if self.active.len() + usize::from(layout.depth) > 64 {
                return Err("Struct layout cycle or nesting exceeds64".into());
            }
            return Ok(Arc::clone(layout));
        }
        if self.active.len() >= 64 || !self.active.insert(identity) {
            return Err("Struct layout cycle or nesting exceeds 64".into());
        }
        let result = self.build(identity);
        self.active.remove(&identity);
        let layout = Arc::new(result?);
        let mut cache = self.resolver.value_layouts.borrow_mut();
        if cache.len() >= 16_384 {
            return Err("Struct layout count exceeds 16384".into());
        }
        cache.insert(identity, Arc::clone(&layout));
        Ok(layout)
    }

    fn shape(&mut self, assembly: usize, ty: &TypeSig) -> Result<(u32, u32, bool, u8), String> {
        if self.resolver.int128_scalar(assembly, ty)?.is_some() {
            return Ok((16, 16, false, 0));
        }
        if self
            .resolver
            .delegate_signature_type(assembly, ty)?
            .is_some()
        {
            return Ok((2, 2, true, 0));
        }
        let bytes = match ty {
            TypeSig::Primitive(ElementType::Boolean | ElementType::I1 | ElementType::U1) => 1,
            TypeSig::Primitive(
                ElementType::Char
                | ElementType::I2
                | ElementType::U2
                | ElementType::IntPtr
                | ElementType::UIntPtr,
            ) => 2,
            TypeSig::Primitive(ElementType::I4 | ElementType::U4 | ElementType::R4) => 4,
            TypeSig::Primitive(ElementType::I8 | ElementType::U8 | ElementType::R8) => 8,
            TypeSig::Ptr(inner) => {
                self.resolver.validate_type(assembly, inner, 0)?;
                2
            }
            TypeSig::ValueType(encoded) => {
                let identity = self.resolver.signature_type(assembly, *encoded)?;
                let raw = self.resolver.type_definition(identity)?;
                let metadata = self.resolver.assembly(raw.assembly)?.metadata();
                let ty = &metadata.type_defs[(raw.token & 0xffffff) as usize - 1];
                if ty.extends.table == Some(TableId::TypeRef)
                    && framework_type(metadata, 0x01000000 | ty.extends.row)?
                        == Some(("System", "Enum"))
                {
                    return self.shape(assembly, &self.resolver.enum_storage(assembly, *encoded)?);
                }
                let layout = self.value(identity)?;
                return Ok((
                    u32::from(layout.bytes),
                    u32::from(layout.alignment),
                    layout.contains_references,
                    layout.depth,
                ));
            }
            TypeSig::Class(_)
            | TypeSig::SzArray(_)
            | TypeSig::Array { .. }
            | TypeSig::Primitive(ElementType::Object | ElementType::String) => {
                self.reference(assembly, ty, 0)?;
                return Ok((2, 2, true, 0));
            }
            TypeSig::ByRef(_) | TypeSig::Pinned(_) => {
                return Err("Managed byref fields require scoped/ref-struct lowering".into());
            }
            _ => return Err(format!("Unsupported unmanaged struct field type {ty:?}")),
        };
        Ok((bytes, bytes, false, 0))
    }

    fn reference_work(&self, amount: usize) -> Result<(), String> {
        let work = self
            .resolver
            .reference_layout_work
            .get()
            .checked_add(amount)
            .filter(|&work| work <= 1_048_576)
            .ok_or("Struct reference layout dependency budget exceeded")?;
        self.resolver.reference_layout_work.set(work);
        Ok(())
    }

    fn reference(&mut self, assembly: usize, ty: &TypeSig, depth: usize) -> Result<(), String> {
        self.reference_work(1)?;
        if depth >= 64 {
            return Err("Struct reference field nesting exceeds64".into());
        }
        if self
            .resolver
            .delegate_signature_type(assembly, ty)?
            .is_some()
        {
            return Ok(());
        }
        match ty {
            TypeSig::Primitive(ElementType::Void) => {
                return Err("Void array elements are invalid".into());
            }
            TypeSig::Primitive(
                ElementType::Boolean
                | ElementType::Char
                | ElementType::I1
                | ElementType::U1
                | ElementType::I2
                | ElementType::U2
                | ElementType::I4
                | ElementType::U4
                | ElementType::I8
                | ElementType::U8
                | ElementType::IntPtr
                | ElementType::UIntPtr
                | ElementType::R4
                | ElementType::R8
                | ElementType::Object
                | ElementType::String,
            ) => {}
            TypeSig::Ptr(inner) => self.resolver.validate_type(assembly, inner, 0)?,
            TypeSig::Class(encoded) | TypeSig::ValueType(encoded) => {
                let id = self.resolver.signature_type(assembly, *encoded)?;
                let raw = self.resolver.type_definition(id)?;
                let metadata = self.resolver.assembly(raw.assembly)?.metadata();
                let row = raw.token & 0xffffff;
                self.reference_work(metadata.generic_params.len())?;
                if id == raw
                    && metadata
                        .generic_params
                        .iter()
                        .any(|p| p.owner.table == Some(TableId::TypeDef) && p.owner.row == row)
                {
                    return Err("Struct reference fields require instantiated generic types".into());
                }
                let definition = &metadata.type_defs[row as usize - 1];
                let value = definition.extends.table == Some(TableId::TypeRef)
                    && matches!(
                        framework_type(metadata, 0x01000000 | definition.extends.row)?,
                        Some(("System", "ValueType" | "Enum"))
                    );
                if value != matches!(ty, TypeSig::ValueType(_)) {
                    return Err(
                        "Struct reference field has invalid class/value signature kind".into(),
                    );
                }
            }
            TypeSig::SzArray(inner)
            | TypeSig::Array {
                element_type: inner,
                ..
            } => self.reference(assembly, inner, depth + 1)?,
            _ => return Err("Unsupported struct reference field shape".into()),
        }
        Ok(())
    }

    fn build(&mut self, identity: Definition) -> Result<Layout, String> {
        let raw = self.resolver.type_definition(identity)?;
        let metadata = self.resolver.assembly(raw.assembly)?.metadata();
        let rid = raw.token & 0xffffff;
        let ty = &metadata.type_defs[rid as usize - 1];
        if ty.extends.table != Some(TableId::TypeRef)
            || framework_type(metadata, 0x01000000 | ty.extends.row)?
                != Some(("System", "ValueType"))
            || ty.flags & 0x100 == 0
            || ty.flags & 0x20 != 0
        {
            return Err("Aggregate must be a sealed framework System.ValueType".into());
        }
        if identity == raw
            && metadata
                .generic_params
                .iter()
                .any(|p| p.owner.table == Some(TableId::TypeDef) && p.owner.row == rid)
        {
            return Err("Unmanaged struct generic shapes are unsupported".into());
        }
        let explicit = match ty.flags & 0x18 {
            8 => false,
            16 => true,
            _ => return Err("AutoLayout structs are unsupported".into()),
        };
        for attribute in &metadata.custom_attributes {
            if attribute.parent.table != Some(TableId::TypeDef)
                || attribute.parent.row != rid
                || attribute.attr_type.table != Some(TableId::MemberRef)
            {
                continue;
            }
            let constructor = attribute
                .attr_type
                .row
                .checked_sub(1)
                .and_then(|row| metadata.member_refs.get(row as usize))
                .ok_or("Invalid struct attribute constructor")?;
            if constructor.class.table == Some(TableId::TypeRef)
                && framework_type(metadata, 0x01000000 | constructor.class.row)?
                    == Some(("System.Runtime.CompilerServices", "InlineArrayAttribute"))
            {
                return Err("InlineArray structs require explicit array lowering".into());
            }
        }
        let mut declarations = metadata
            .class_layouts
            .iter()
            .filter(|entry| entry.parent == rid);
        let declaration = declarations.next();
        if declarations.next().is_some() {
            return Err("Duplicate ClassLayout for struct".into());
        }
        let packing = declaration.map(|entry| entry.packing_size).unwrap_or(0);
        if (packing != 0 && (!packing.is_power_of_two() || packing > 128))
            || (explicit && packing != 0)
        {
            return Err("Invalid struct packing size".into());
        }
        let packing = u32::from(if packing == 0 { 8 } else { packing });
        let declared_size = declaration.map(|entry| entry.class_size).unwrap_or(0);
        if declared_size > 4096 {
            return Err("Struct size exceeds 4096-byte target capacity".into());
        }
        let end = metadata
            .type_defs
            .get(rid as usize)
            .map(|ty| ty.field_list)
            .unwrap_or(metadata.fields.len() as u32 + 1);
        if end - ty.field_list > 4096 {
            return Err("Struct field count exceeds 4096".into());
        }
        let mut bytes = 0u32;
        let mut alignment = 1u32;
        let mut fields = Vec::new();
        let mut contains_references = false;
        let mut depth = 1u8;
        for index in ty.field_list..end {
            self.fields += 1;
            if self.fields > 1_048_576 {
                return Err("Struct layout dependency budget exceeded".into());
            }
            let field = &metadata.fields[index as usize - 1];
            let mut offsets = metadata
                .field_layouts
                .iter()
                .filter(|entry| entry.field == index);
            let offset = offsets.next().map(|entry| entry.offset);
            if offsets.next().is_some() {
                return Err("Duplicate FieldLayout for struct field".into());
            }
            if field.flags & 0x10 != 0 {
                if offset.is_some() {
                    return Err("Static struct field has FieldLayout".into());
                }
                continue;
            }
            let signature = signature::field(
                metadata
                    .blobs
                    .get(field.signature)
                    .map_err(|e| e.to_string())?,
            )?;
            let arguments = self
                .resolver
                .type_instance(identity)?
                .map_or_else(Vec::new, |instance| instance.arguments);
            let signature =
                self.resolver
                    .bind_signature(raw.assembly, &signature, &[], &arguments)?;
            let (size, natural, references, child_depth) =
                self.shape(signature.assembly, &signature.ty)?;
            depth = depth.max(
                child_depth
                    .checked_add(1)
                    .filter(|&value| value <= 64)
                    .ok_or("Struct layout nesting exceeds64")?,
            );
            contains_references |= references;
            if explicit && references {
                return Err(
                    "Explicit layouts containing managed references are unsupported".into(),
                );
            }
            let field_alignment = natural.min(packing);
            alignment = alignment.max(field_alignment);
            let offset = if explicit {
                offset.ok_or("Explicit struct field is missing FieldLayout")?
            } else {
                if offset.is_some() {
                    return Err("Sequential struct field has FieldLayout".into());
                }
                aligned(bytes, field_alignment)?
            };
            bytes = bytes.max(
                offset
                    .checked_add(size)
                    .ok_or("Struct field extent overflow")?,
            );
            if bytes > 4096 {
                return Err("Struct size exceeds 4096-byte target capacity".into());
            }
            fields.push(Field {
                identity: self.resolver.close_field(
                    Definition {
                        assembly: identity.assembly,
                        token: 0x04000000 | index,
                    },
                    identity,
                )?,
                offset: offset as u16,
                signature: signature.ty,
                signature_assembly: signature.assembly,
            });
        }
        let bytes = if declared_size == 0 {
            aligned(bytes.max(1), alignment)?
        } else {
            bytes.max(declared_size).max(1)
        };
        if bytes > 4096 {
            return Err("Struct size exceeds 4096-byte target capacity".into());
        }
        Ok(Layout {
            identity,
            bytes: bytes as u16,
            alignment: alignment as u8,
            fields,
            contains_references,
            depth,
        })
    }
}
