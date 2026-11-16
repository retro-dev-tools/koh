use super::{Build, Field};
use crate::{
    resolve::{Definition, Resolver, framework_type, type_token},
    signature,
};
use clrmeta::{ElementType, TableId, TypeSig};
use std::{collections::HashSet, sync::Arc};

#[derive(Debug, Clone)]
pub struct ClassLayout {
    pub identity: Definition,
    pub base: Option<Definition>,
    pub bytes: u16,
    pub fields: Vec<Arc<Field>>,
    depth: u8,
}

pub fn class(resolver: &Resolver<'_>, identity: Definition) -> Result<Arc<ClassLayout>, String> {
    Classes {
        resolver,
        active: HashSet::new(),
    }
    .layout(identity)
}

struct Classes<'r, 'a> {
    resolver: &'r Resolver<'a>,
    active: HashSet<Definition>,
}

impl Classes<'_, '_> {
    fn work(&self, cost: usize) -> Result<(), String> {
        let work = self
            .resolver
            .class_layout_work
            .get()
            .checked_add(cost)
            .filter(|&total| total <= 1_048_576)
            .ok_or("Class layout dependency budget exceeded")?;
        self.resolver.class_layout_work.set(work);
        Ok(())
    }

    fn layout(&mut self, identity: Definition) -> Result<Arc<ClassLayout>, String> {
        if identity.token >> 24 == 0x1b {
            return Err("Class TypeSpec requires instantiated generic lowering".into());
        }
        let identity = self
            .resolver
            .resolve_type(identity.assembly, identity.token)?;
        if let Some(layout) = self.resolver.class_layouts.borrow().get(&identity) {
            return Ok(Arc::clone(layout));
        }
        if self.active.len() >= 64 || !self.active.insert(identity) {
            return Err("Class inheritance cycle or depth exceeds64".into());
        }
        let result = self.build(identity);
        self.active.remove(&identity);
        let layout = Arc::new(result?);
        let mut cache = self.resolver.class_layouts.borrow_mut();
        if cache.len() >= 16_384 {
            return Err("Class layout count exceeds16384".into());
        }
        cache.insert(identity, Arc::clone(&layout));
        Ok(layout)
    }

    fn field_size(&self, assembly: usize, ty: &TypeSig, depth: usize) -> Result<u16, String> {
        self.work(1)?;
        if depth >= 64 {
            return Err("Class field type nesting exceeds64".into());
        }
        if self
            .resolver
            .delegate_signature_type(assembly, ty)?
            .is_some()
        {
            return Ok(2);
        }
        match ty {
            TypeSig::Primitive(ElementType::Object | ElementType::String) => Ok(2),
            TypeSig::Primitive(ElementType::R4) => Ok(4),
            TypeSig::Primitive(ElementType::R8) => Ok(8),
            TypeSig::Class(encoded) => {
                let identity = self
                    .resolver
                    .resolve_type(assembly, type_token(*encoded)?)?;
                let metadata = self.resolver.assembly(identity.assembly)?.metadata();
                self.work(metadata.generic_params.len())?;
                let row = identity.token & 0xffffff;
                let definition = &metadata.type_defs[row as usize - 1];
                if definition.extends.table == Some(TableId::TypeRef)
                    && matches!(
                        framework_type(metadata, 0x01000000 | definition.extends.row)?,
                        Some(("System", "ValueType" | "Enum"))
                    )
                {
                    return Err("Class field reference cannot name a value type".into());
                }
                if metadata
                    .generic_params
                    .iter()
                    .any(|p| p.owner.table == Some(TableId::TypeDef) && p.owner.row == row)
                {
                    return Err("Class field requires instantiated generic type".into());
                }
                Ok(2)
            }
            TypeSig::SzArray(element)
            | TypeSig::Array {
                element_type: element,
                ..
            } => {
                Build {
                    resolver: self.resolver,
                    active: HashSet::new(),
                    fields: 0,
                }
                .reference(assembly, element, depth + 1)?;
                Ok(2)
            }
            TypeSig::ByRef(_) | TypeSig::Pinned(_) => {
                Err("Class fields cannot contain managed byrefs or pins".into())
            }
            TypeSig::GenericInst { .. } | TypeSig::Var(_) | TypeSig::MVar(_) => {
                Err("Class field generic shapes require instantiated lowering".into())
            }
            _ => {
                let (size, _, _, _) = Build {
                    resolver: self.resolver,
                    active: HashSet::new(),
                    fields: 0,
                }
                .shape(assembly, ty)?;
                u16::try_from(size).map_err(|_| "Class field exceeds target capacity".into())
            }
        }
    }

    fn build(&mut self, identity: Definition) -> Result<ClassLayout, String> {
        let metadata = self.resolver.assembly(identity.assembly)?.metadata();
        let rid = identity.token & 0xffffff;
        let ty = &metadata.type_defs[rid as usize - 1];
        self.work(
            1 + metadata.generic_params.len()
                + metadata.class_layouts.len()
                + metadata.field_layouts.len(),
        )?;
        if ty.flags & 0x20 != 0 {
            return Err("Class layout cannot describe an interface".into());
        }
        let framework = if ty.extends.table == Some(TableId::TypeRef) {
            framework_type(metadata, 0x01000000 | ty.extends.row)?
        } else {
            None
        };
        if matches!(framework, Some(("System", "ValueType" | "Enum"))) {
            return Err("Object layout requires a reference class, not a value type".into());
        }
        if ty.flags & 0x18 != 0 {
            return Err("Reference class layout currently requires AutoLayout".into());
        }
        if metadata
            .generic_params
            .iter()
            .any(|p| p.owner.table == Some(TableId::TypeDef) && p.owner.row == rid)
        {
            return Err("Class generic shapes require instantiated lowering".into());
        }
        if metadata.class_layouts.iter().any(|row| row.parent == rid) {
            return Err("AutoLayout class has unexpected ClassLayout metadata".into());
        }
        let base = if framework == Some(("System", "Object")) {
            None
        } else {
            let table = ty.extends.table.ok_or("Reference class has no base type")?;
            if ty.extends.row == 0 {
                return Err("Reference class has no base type".into());
            }
            if table == TableId::TypeSpec {
                return Err("Class base generic shapes require instantiated lowering".into());
            }
            Some(
                self.resolver
                    .resolve_type(identity.assembly, ((table as u32) << 24) | ty.extends.row)?,
            )
        };
        let (mut bytes, mut fields, depth) = if let Some(base) = base {
            let layout = self.layout(base)?;
            if layout.depth >= 64 {
                return Err("Class inheritance depth exceeds64".into());
            }
            self.work(layout.fields.len())?;
            let parent = self.resolver.assembly(base.assembly)?.metadata();
            if parent.type_defs[(base.token & 0xffffff) as usize - 1].flags & 0x100 != 0 {
                return Err("Class cannot inherit a sealed base".into());
            }
            (
                u32::from(layout.bytes),
                layout.fields.clone(),
                layout.depth + 1,
            )
        } else {
            (2, Vec::new(), 1)
        };
        let end = metadata
            .type_defs
            .get(rid as usize)
            .map(|ty| ty.field_list)
            .unwrap_or(metadata.fields.len() as u32 + 1);
        if end - ty.field_list > 4096 {
            return Err("Class field count exceeds4096".into());
        }
        self.work((end - ty.field_list) as usize)?;
        let explicit_fields: HashSet<_> = metadata
            .field_layouts
            .iter()
            .map(|entry| entry.field)
            .collect();
        for index in ty.field_list..end {
            let row = &metadata.fields[index as usize - 1];
            if explicit_fields.contains(&index) {
                return Err("AutoLayout class field has unexpected FieldLayout metadata".into());
            }
            if row.flags & 0x10 != 0 {
                continue;
            }
            if row.flags & (0x40 | 0x100) != 0 {
                return Err("Class instance field cannot be literal or RVA".into());
            }
            let blob = metadata
                .blobs
                .get(row.signature)
                .map_err(|e| e.to_string())?;
            self.work(blob.len())?;
            let signature = signature::field(blob)?;
            let size = self.field_size(identity.assembly, &signature, 0)?;
            let offset = bytes;
            bytes = bytes
                .checked_add(u32::from(size))
                .ok_or("Class field extent overflow")?;
            if bytes > 4096 {
                return Err("Class size exceeds4096-byte target capacity".into());
            }
            fields.push(Arc::new(Field {
                identity: Definition {
                    assembly: identity.assembly,
                    token: 0x04000000 | index,
                },
                offset: offset as u16,
                signature,
            }));
        }
        Ok(ClassLayout {
            identity,
            base,
            bytes: bytes as u16,
            fields,
            depth,
        })
    }
}
