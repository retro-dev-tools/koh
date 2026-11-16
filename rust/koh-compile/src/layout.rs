use crate::{
    resolve::{Definition, Resolver, framework_type, type_token},
    signature,
};
use clrmeta::{ElementType, TableId, TypeSig};
use std::{collections::HashSet, sync::Arc};
mod classes;
pub use classes::{ClassLayout, class};

#[derive(Debug, Clone)]
pub struct Field {
    pub identity: Definition,
    pub offset: u16,
    pub signature: TypeSig,
}

#[derive(Debug, Clone)]
pub struct Layout {
    pub identity: Definition,
    pub bytes: u16,
    pub alignment: u8,
    pub fields: Vec<Field>,
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

    fn shape(&mut self, assembly: usize, ty: &TypeSig) -> Result<(u32, u32), String> {
        let bytes = match ty {
            TypeSig::Primitive(ElementType::Boolean | ElementType::I1 | ElementType::U1) => 1,
            TypeSig::Primitive(
                ElementType::Char
                | ElementType::I2
                | ElementType::U2
                | ElementType::IntPtr
                | ElementType::UIntPtr,
            ) => 2,
            TypeSig::Primitive(ElementType::I4 | ElementType::U4) => 4,
            TypeSig::Primitive(ElementType::I8 | ElementType::U8) => 8,
            TypeSig::Ptr(inner) => {
                self.resolver.validate_type(assembly, inner, 0)?;
                2
            }
            TypeSig::ValueType(encoded) => {
                let identity = self
                    .resolver
                    .resolve_type(assembly, type_token(*encoded)?)?;
                let metadata = self.resolver.assembly(identity.assembly)?.metadata();
                let ty = &metadata.type_defs[(identity.token & 0xffffff) as usize - 1];
                if ty.extends.table == Some(TableId::TypeRef)
                    && framework_type(metadata, 0x01000000 | ty.extends.row)?
                        == Some(("System", "Enum"))
                {
                    return self.shape(assembly, &self.resolver.enum_storage(assembly, *encoded)?);
                }
                let layout = self.value(identity)?;
                return Ok((u32::from(layout.bytes), u32::from(layout.alignment)));
            }
            TypeSig::Class(_)
            | TypeSig::SzArray(_)
            | TypeSig::Array { .. }
            | TypeSig::ByRef(_)
            | TypeSig::Primitive(ElementType::Object | ElementType::String) => {
                return Err("Managed reference fields are unsupported in unmanaged structs".into());
            }
            _ => return Err(format!("Unsupported unmanaged struct field type {ty:?}")),
        };
        Ok((bytes, bytes))
    }

    fn build(&mut self, identity: Definition) -> Result<Layout, String> {
        let metadata = self.resolver.assembly(identity.assembly)?.metadata();
        let rid = identity.token & 0xffffff;
        let ty = &metadata.type_defs[rid as usize - 1];
        if ty.extends.table != Some(TableId::TypeRef)
            || framework_type(metadata, 0x01000000 | ty.extends.row)?
                != Some(("System", "ValueType"))
            || ty.flags & 0x100 == 0
            || ty.flags & 0x20 != 0
        {
            return Err("Aggregate must be a sealed framework System.ValueType".into());
        }
        if metadata
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
            let (size, natural) = self.shape(identity.assembly, &signature)?;
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
                identity: Definition {
                    assembly: identity.assembly,
                    token: 0x04000000 | index,
                },
                offset: offset as u16,
                signature,
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
        })
    }
}
