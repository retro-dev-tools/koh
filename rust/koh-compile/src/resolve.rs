use crate::assembly::Assembly;
use clrmeta::{CodedIndex, Metadata, MethodSig, TableId, TypeSig};
use std::cell::RefCell;
use std::collections::HashMap;
mod index;
use index::Index;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Definition {
    pub assembly: usize,
    pub token: u32,
}

#[derive(Debug)]
pub struct ResolvedMethod {
    pub definition: Definition,
    pub type_arguments: Vec<TypeSig>,
    pub method_arguments: Vec<TypeSig>,
    pub argument_assembly: usize,
}

#[derive(Debug)]
pub struct ResolvedField {
    pub definition: Definition,
    pub type_arguments: Vec<TypeSig>,
    pub argument_assembly: usize,
}

pub struct Resolver<'a> {
    assemblies: Vec<Assembly<'a>>,
    identities: HashMap<Identity, usize>,
    indices: Vec<Index>,
    type_cache: RefCell<HashMap<Definition, Definition>>,
}

#[derive(Debug, PartialEq, Eq, Hash)]
struct Identity {
    name: String,
    version: [u16; 4],
    culture: String,
    key: Option<[u8; 8]>,
}

fn key(bytes: &[u8], full: bool) -> Result<Option<[u8; 8]>, String> {
    if bytes.is_empty() {
        if full {
            return Err("PublicKey flag has no public key".into());
        }
        return Ok(None);
    }
    if full {
        Ok(Some(clrmeta::crypto::public_key_token(bytes)))
    } else {
        bytes
            .try_into()
            .map(Some)
            .map_err(|_| "Public key token must contain eight bytes".into())
    }
}

fn row<T>(rows: &[T], token: u32, table: u8) -> Result<&T, String> {
    if token >> 24 != u32::from(table) {
        return Err(format!(
            "Expected table {table:02x}, got token 0x{token:08x}"
        ));
    }
    (token & 0x00ff_ffff)
        .checked_sub(1)
        .and_then(|index| rows.get(index as usize))
        .ok_or_else(|| format!("Invalid metadata token 0x{token:08x}"))
}

fn coded(index: CodedIndex) -> Result<u32, String> {
    let table = index.table.ok_or("Invalid coded metadata index tag")?;
    if index.row == 0 || index.row > 0x00ff_ffff {
        return Err("Invalid coded metadata row".into());
    }
    Ok(((table as u32) << 24) | index.row)
}

fn null_index(index: CodedIndex, zero_table: TableId) -> Result<bool, String> {
    if index.row != 0 {
        return Ok(false);
    }
    if index.table != Some(zero_table) {
        return Err("Nonzero coded index has row zero".into());
    }
    Ok(true)
}

fn type_token(value: u32) -> Result<u32, String> {
    let table = match value & 3 {
        0 => 2,
        1 => 1,
        2 => 0x1b,
        _ => return Err("Invalid type signature token tag".into()),
    };
    coded(CodedIndex {
        table: Some(TableId::from_u8(table).map_err(|e| e.to_string())?),
        row: value >> 2,
    })
}

fn identity(metadata: &Metadata) -> Result<Identity, String> {
    let assembly = metadata
        .assemblies
        .first()
        .ok_or("Missing assembly definition")?;
    Ok(Identity {
        name: metadata
            .strings
            .get(assembly.name)
            .map_err(|e| e.to_string())?
            .to_ascii_lowercase(),
        version: [
            assembly.major_version,
            assembly.minor_version,
            assembly.build_number,
            assembly.revision_number,
        ],
        culture: metadata
            .strings
            .get(assembly.culture)
            .map_err(|e| e.to_string())?
            .to_ascii_lowercase(),
        key: key(
            metadata
                .blobs
                .get(assembly.public_key)
                .map_err(|e| e.to_string())?,
            assembly.flags & 1 != 0,
        )?,
    })
}

impl<'a> Resolver<'a> {
    pub(crate) fn primitive(
        &self,
        assembly: usize,
        token: u32,
    ) -> Result<crate::ir::Scalar, String> {
        use crate::ir::Scalar;
        let metadata = self.assembly(assembly)?.metadata();
        let ty = row(&metadata.type_refs, token, 1)?;
        if ty.resolution_scope.table != Some(TableId::AssemblyRef) {
            return Err("Array element requires a framework primitive type".into());
        }
        let reference = row(&metadata.assembly_refs, coded(ty.resolution_scope)?, 0x23)?;
        let string = |index| metadata.strings.get(index).map_err(|e| e.to_string());
        let name = string(reference.name)?;
        let expected = match name {
            "System.Runtime" => [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a],
            "System.Private.CoreLib" => [0x7c, 0xec, 0x85, 0xd7, 0xbe, 0xa7, 0x79, 0x8e],
            "mscorlib" => [0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89],
            _ => return Err("Array element is not a supported framework primitive".into()),
        };
        if reference.flags & 0x0f00 != 0
            || !string(reference.culture)?.is_empty()
            || key(
                metadata
                    .blobs
                    .get(reference.public_key_or_token)
                    .map_err(|e| e.to_string())?,
                reference.flags & 1 != 0,
            )? != Some(expected)
            || string(ty.type_namespace)? != "System"
        {
            return Err("Invalid framework primitive identity".into());
        }
        Ok(match string(ty.type_name)? {
            "Boolean" => Scalar::Bool,
            "Char" => Scalar::Char,
            "SByte" => Scalar::I8,
            "Byte" => Scalar::U8,
            "Int16" => Scalar::I16,
            "UInt16" => Scalar::U16,
            "Int32" => Scalar::I32,
            "UInt32" => Scalar::U32,
            "Int64" => Scalar::I64,
            "UInt64" => Scalar::U64,
            "IntPtr" => Scalar::NativeSigned,
            "UIntPtr" => Scalar::NativeUnsigned,
            _ => return Err("Array element is not a supported integer primitive".into()),
        })
    }
    pub fn new(images: &[&'a [u8]]) -> Result<Self, String> {
        if images.len() > 1024
            || images
                .iter()
                .try_fold(0usize, |sum, bytes| sum.checked_add(bytes.len()))
                .is_none_or(|size| size > 256 * 1024 * 1024)
        {
            return Err("Reference inputs exceed 1024 assemblies or 256 MiB".into());
        }
        let assemblies: Vec<_> = images
            .iter()
            .map(|bytes| Assembly::parse(bytes))
            .collect::<Result<_, _>>()?;
        let mut identities = HashMap::new();
        let mut indices = Vec::new();
        let mut name_budget = 0;
        for (index, assembly) in assemblies.iter().enumerate() {
            if identities
                .insert(identity(assembly.metadata())?, index)
                .is_some()
            {
                return Err(format!("Duplicate assembly identity: {}", assembly.name()?));
            }
            let metadata = assembly.metadata();
            let mut previous = 1;
            for ty in &metadata.type_defs {
                if ty.field_list < previous || ty.field_list as usize > metadata.fields.len() + 1 {
                    return Err(format!("{}: invalid TypeDef field range", assembly.name()?));
                }
                previous = ty.field_list;
            }
            if !metadata.fields.is_empty()
                && metadata
                    .type_defs
                    .first()
                    .is_none_or(|ty| ty.field_list != 1)
            {
                return Err("Fields have no declaring type".into());
            }
            indices.push(Index::new(metadata, &mut name_budget)?);
        }
        Ok(Self {
            assemblies,
            identities,
            indices,
            type_cache: RefCell::new(HashMap::new()),
        })
    }

    pub fn assembly(&self, index: usize) -> Result<&Assembly<'a>, String> {
        self.assemblies
            .get(index)
            .ok_or_else(|| format!("Invalid assembly index {index}"))
    }

    pub fn assembly_count(&self) -> usize {
        self.assemblies.len()
    }

    pub fn method_initializer(&self, method: Definition) -> Result<Option<Definition>, String> {
        let assembly = self.assembly(method.assembly)?;
        assembly.method(method.token)?;
        let rid = method.token & 0x00ff_ffff;
        let owner = assembly
            .metadata()
            .type_defs
            .partition_point(|ty| ty.method_list <= rid);
        if owner == 0 {
            return Err("Method has no declaring type".into());
        }
        if assembly.metadata().type_defs[owner - 1].flags & 0x100000 != 0 {
            return Ok(None);
        }
        self.initializer(method.assembly, owner as u32)
    }

    pub fn module_initializer(&self, assembly: usize) -> Result<Option<Definition>, String> {
        self.initializer(assembly, 1)
    }

    pub fn field_initializer(&self, field: Definition) -> Result<Option<Definition>, String> {
        let metadata = self.assembly(field.assembly)?.metadata();
        row(&metadata.fields, field.token, 4)?;
        let owner = metadata
            .type_defs
            .partition_point(|ty| ty.field_list <= field.token & 0xffffff);
        if owner == 0 {
            return Err("Field has no declaring type".into());
        }
        self.initializer(field.assembly, owner as u32)
    }

    fn initializer(&self, assembly: usize, owner: u32) -> Result<Option<Definition>, String> {
        let methods = self
            .index(assembly)?
            .methods
            .get(&owner)
            .and_then(|names| names.get(".cctor"));
        match methods.map(Vec::as_slice).unwrap_or(&[]) {
            [] => Ok(None),
            [token] => Ok(Some(Definition {
                assembly,
                token: *token,
            })),
            _ => Err("Type has multiple initializers".into()),
        }
    }

    fn index(&self, assembly: usize) -> Result<&Index, String> {
        self.indices
            .get(assembly)
            .ok_or_else(|| format!("Invalid assembly index {assembly}"))
    }

    fn reference(&self, assembly: usize, token: u32) -> Result<usize, String> {
        let metadata = self.assembly(assembly)?.metadata();
        let reference = row(&metadata.assembly_refs, token, 0x23)?;
        let name = metadata
            .strings
            .get(reference.name)
            .map_err(|e| e.to_string())?;
        if reference.flags & 0x0f00 != 0 {
            return Err(format!(
                "{name}: retargetable/Windows Runtime references are unsupported"
            ));
        }
        let expected = Identity {
            name: name.to_ascii_lowercase(),
            version: [
                reference.major_version,
                reference.minor_version,
                reference.build_number,
                reference.revision_number,
            ],
            culture: metadata
                .strings
                .get(reference.culture)
                .map_err(|e| e.to_string())?
                .to_ascii_lowercase(),
            key: key(
                metadata
                    .blobs
                    .get(reference.public_key_or_token)
                    .map_err(|e| e.to_string())?,
                reference.flags & 1 != 0,
            )?,
        };
        self.identities.get(&expected).copied().ok_or_else(|| {
            format!(
                "Missing assembly {name}, version {}.{}.{}.{}, culture {:?}, public key token {:?}",
                expected.version[0],
                expected.version[1],
                expected.version[2],
                expected.version[3],
                expected.culture,
                expected.key
            )
        })
    }

    fn find_type(
        &self,
        assembly: usize,
        namespace: &str,
        name: &str,
        parent: Option<Definition>,
        depth: usize,
    ) -> Result<Definition, String> {
        if depth >= 64 {
            return Err("Type resolution cycle or depth exceeds 64".into());
        }
        let index = self.index(assembly)?;
        let mut found = None;
        let parent_row = parent.map_or(0, |parent| parent.token & 0x00ff_ffff);
        if let Some(&token) = index
            .types
            .get(&parent_row)
            .and_then(|namespaces| namespaces.get(namespace))
            .and_then(|names| names.get(name))
        {
            return Ok(Definition { assembly, token });
        }
        if parent.is_none() {
            if let Some(exports) = index
                .exports
                .get(namespace)
                .and_then(|names| names.get(name))
            {
                for &token in exports {
                    if found
                        .replace(self.resolve_type_inner(assembly, token, depth + 1)?)
                        .is_some()
                    {
                        return Err(format!("Ambiguous forwarded type {namespace}.{name}"));
                    }
                }
            }
        }
        found.ok_or_else(|| {
            format!(
                "{}: missing type {namespace}.{name}",
                self.assembly(assembly)
                    .and_then(Assembly::name)
                    .unwrap_or("<invalid assembly>")
            )
        })
    }

    pub fn resolve_type(&self, assembly: usize, token: u32) -> Result<Definition, String> {
        self.resolve_type_inner(assembly, token, 0)
    }

    fn resolve_type_inner(
        &self,
        assembly: usize,
        token: u32,
        depth: usize,
    ) -> Result<Definition, String> {
        if depth >= 64 {
            return Err("Type resolution cycle or depth exceeds 64".into());
        }
        let id = Definition { assembly, token };
        if let Some(&definition) = self.type_cache.borrow().get(&id) {
            return Ok(definition);
        }
        let metadata = self.assembly(assembly)?.metadata();
        let result = match token >> 24 {
            2 => {
                row(&metadata.type_defs, token, 2)?;
                Ok(Definition { assembly, token })
            }
            1 => {
                let ty = row(&metadata.type_refs, token, 1)?;
                let name = metadata
                    .strings
                    .get(ty.type_name)
                    .map_err(|e| e.to_string())?;
                let namespace = metadata
                    .strings
                    .get(ty.type_namespace)
                    .map_err(|e| e.to_string())?;
                let (target, parent) = if null_index(ty.resolution_scope, TableId::Module)? {
                    (assembly, None)
                } else {
                    match ty.resolution_scope.table {
                        Some(TableId::AssemblyRef) => {
                            (self.reference(assembly, coded(ty.resolution_scope)?)?, None)
                        }
                        Some(TableId::Module) if ty.resolution_scope.row == 1 => (assembly, None),
                        Some(TableId::TypeRef) => {
                            let parent = self.resolve_type_inner(
                                assembly,
                                coded(ty.resolution_scope)?,
                                depth + 1,
                            )?;
                            (parent.assembly, Some(parent))
                        }
                        _ => {
                            return Err(format!(
                                "{namespace}.{name}: unsupported or invalid resolution scope"
                            ));
                        }
                    }
                };
                self.find_type(target, namespace, name, parent, depth + 1)
            }
            0x1b => {
                let ty = row(&metadata.type_specs, token, 0x1b)?;
                let signature = crate::signature::type_spec(
                    metadata
                        .blobs
                        .get(ty.signature)
                        .map_err(|e| e.to_string())?,
                )?;
                match signature {
                    TypeSig::Class(value) | TypeSig::ValueType(value) => self.resolve_type_inner(assembly, type_token(value)?, depth + 1),
                    TypeSig::GenericInst { type_ref, type_args, .. } => {
                        let definition = self.resolve_type_inner(assembly, type_token(type_ref)?, depth + 1)?;
                        self.check_arity(definition, type_args.len())?;
                        for argument in &type_args { self.validate_type(assembly, argument, depth + 1)?; }
                        Ok(definition)
                    }
                    _ => Err("TypeSpec does not identify a named type; array/runtime operations require dedicated lowering".into()),
                }
            }
            0x27 => {
                let exported = row(&metadata.exported_types, token, 0x27)?;
                let name = metadata
                    .strings
                    .get(exported.type_name)
                    .map_err(|e| e.to_string())?;
                let namespace = metadata
                    .strings
                    .get(exported.type_namespace)
                    .map_err(|e| e.to_string())?;
                match exported.implementation.table {
                    Some(TableId::AssemblyRef) if exported.flags & 0x00200000 != 0 => {
                        let target = self.reference(assembly, coded(exported.implementation)?)?;
                        self.find_type(target, namespace, name, None, depth + 1)
                    }
                    Some(TableId::ExportedType) => {
                        let parent = self.resolve_type_inner(
                            assembly,
                            coded(exported.implementation)?,
                            depth + 1,
                        )?;
                        self.find_type(parent.assembly, namespace, name, Some(parent), depth + 1)
                    }
                    _ => Err("Unsupported multi-module or invalid exported type".into()),
                }
            }
            _ => Err(format!("Not a type token: 0x{token:08x}")),
        };
        if let Ok(definition) = &result {
            self.type_cache.borrow_mut().insert(id, *definition);
        }
        result
    }

    fn check_arity(&self, definition: Definition, expected: usize) -> Result<(), String> {
        let table = TableId::from_u8((definition.token >> 24) as u8).map_err(|e| e.to_string())?;
        let actual = self
            .index(definition.assembly)?
            .generic_arity
            .get(&(table, definition.token & 0x00ff_ffff))
            .copied()
            .unwrap_or(0);
        if actual != expected {
            return Err(format!(
                "Generic arity disagrees for token 0x{:08x}: expected {expected}, metadata has {actual}",
                definition.token
            ));
        }
        Ok(())
    }

    fn validate_method_types(
        &self,
        assembly: usize,
        signature: &MethodSig,
        depth: usize,
    ) -> Result<(), String> {
        self.validate_type(assembly, &signature.return_type, depth + 1)?;
        for parameter in &signature.params {
            self.validate_type(assembly, parameter, depth + 1)?;
        }
        Ok(())
    }

    fn validate_type(&self, assembly: usize, ty: &TypeSig, depth: usize) -> Result<(), String> {
        if depth >= 64 {
            return Err("Type validation cycle or nesting exceeds 64".into());
        }
        match ty {
            TypeSig::Primitive(_) | TypeSig::Var(_) | TypeSig::MVar(_) => {}
            TypeSig::Class(token) | TypeSig::ValueType(token) => {
                self.resolve_type_inner(assembly, type_token(*token)?, depth + 1)?;
            }
            TypeSig::GenericInst {
                type_ref,
                type_args,
                ..
            } => {
                let definition =
                    self.resolve_type_inner(assembly, type_token(*type_ref)?, depth + 1)?;
                self.check_arity(definition, type_args.len())?;
                for argument in type_args {
                    self.validate_type(assembly, argument, depth + 1)?;
                }
            }
            TypeSig::Ptr(inner)
            | TypeSig::ByRef(inner)
            | TypeSig::Pinned(inner)
            | TypeSig::SzArray(inner)
            | TypeSig::Array {
                element_type: inner,
                ..
            } => self.validate_type(assembly, inner, depth + 1)?,
            TypeSig::Modified {
                modifier, inner, ..
            } => {
                self.resolve_type_inner(assembly, type_token(*modifier)?, depth + 1)?;
                self.validate_type(assembly, inner, depth + 1)?;
            }
            TypeSig::FnPtr(signature) => {
                self.validate_method_types(assembly, signature, depth + 1)?
            }
            _ => return Err("Unsupported signature type variant".into()),
        }
        Ok(())
    }

    fn parent(&self, assembly: usize, token: u32) -> Result<(Definition, Vec<TypeSig>), String> {
        let definition = self.resolve_type(assembly, token)?;
        let arguments = if token >> 24 == 0x1b {
            let metadata = self.assembly(assembly)?.metadata();
            let ty = row(&metadata.type_specs, token, 0x1b)?;
            match crate::signature::type_spec(
                metadata
                    .blobs
                    .get(ty.signature)
                    .map_err(|e| e.to_string())?,
            )? {
                TypeSig::GenericInst { type_args, .. } => type_args,
                _ => Vec::new(),
            }
        } else {
            Vec::new()
        };
        Ok((definition, arguments))
    }

    pub fn resolve_method(&self, assembly: usize, token: u32) -> Result<ResolvedMethod, String> {
        let metadata = self.assembly(assembly)?.metadata();
        match token >> 24 {
            6 => {
                let signature = self.assembly(assembly)?.method_signature(token)?;
                let definition = Definition { assembly, token };
                self.check_arity(definition, signature.generic_param_count as usize)?;
                self.validate_method_types(assembly, &signature, 0)?;
                Ok(ResolvedMethod {
                    definition,
                    type_arguments: Vec::new(),
                    method_arguments: Vec::new(),
                    argument_assembly: assembly,
                })
            }
            0x2b => {
                let specification = row(&metadata.method_specs, token, 0x2b)?;
                if !matches!(
                    specification.method.table,
                    Some(TableId::MethodDef | TableId::MemberRef)
                ) {
                    return Err("Invalid MethodSpec target".into());
                }
                let arguments = crate::signature::method_spec(
                    metadata
                        .blobs
                        .get(specification.instantiation)
                        .map_err(|e| e.to_string())?,
                )?;
                let mut method = self.resolve_method(assembly, coded(specification.method)?)?;
                self.check_arity(method.definition, arguments.len())?;
                for argument in &arguments {
                    self.validate_type(assembly, argument, 0)?;
                }
                method.method_arguments = arguments;
                Ok(method)
            }
            0x0a => {
                let reference = row(&metadata.member_refs, token, 0x0a)?;
                let name = metadata
                    .strings
                    .get(reference.name)
                    .map_err(|e| e.to_string())?;
                let signature = crate::signature::method(
                    metadata
                        .blobs
                        .get(reference.signature)
                        .map_err(|e| e.to_string())?,
                )?;
                self.validate_method_types(assembly, &signature, 0)?;
                let (mut owner, type_arguments) = self.parent(assembly, coded(reference.class)?)?;
                for _ in 0..64 {
                    let image = self.assembly(owner.assembly)?;
                    let owner_metadata = image.metadata();
                    let mut found = None;
                    let mut signature_bytes = 0usize;
                    for &candidate_token in self
                        .index(owner.assembly)?
                        .methods
                        .get(&(owner.token & 0x00ff_ffff))
                        .and_then(|names| names.get(name))
                        .map(Vec::as_slice)
                        .unwrap_or(&[])
                    {
                        let candidate_row = row(&owner_metadata.method_defs, candidate_token, 6)?;
                        signature_bytes = signature_bytes
                            .checked_add(
                                owner_metadata
                                    .blobs
                                    .get(candidate_row.signature)
                                    .map_err(|e| e.to_string())?
                                    .len(),
                            )
                            .ok_or("Signature lookup overflow")?;
                        if signature_bytes > 4 * 1024 * 1024 {
                            return Err(
                                "Method overload search exceeds 4 MiB signature budget".into()
                            );
                        }
                        let candidate = image.method_signature(candidate_token)?;
                        self.validate_method_types(owner.assembly, &candidate, 0)?;
                        if self.methods_equal(
                            assembly,
                            &signature,
                            owner.assembly,
                            &candidate,
                            0,
                        )? {
                            if found
                                .replace(Definition {
                                    assembly: owner.assembly,
                                    token: candidate_token,
                                })
                                .is_some()
                            {
                                return Err(format!("Ambiguous method {name}"));
                            }
                        }
                    }
                    if let Some(definition) = found {
                        self.check_arity(definition, signature.generic_param_count as usize)?;
                        return Ok(ResolvedMethod {
                            definition,
                            type_arguments,
                            method_arguments: Vec::new(),
                            argument_assembly: assembly,
                        });
                    }
                    let ty = row(&owner_metadata.type_defs, owner.token, 2)?;
                    if null_index(ty.extends, TableId::TypeDef)? {
                        break;
                    }
                    if !type_arguments.is_empty() || ty.extends.table == Some(TableId::TypeSpec) {
                        return Err(format!(
                            "{name}: inherited generic substitution requires typed binding"
                        ));
                    }
                    owner = self.resolve_type(owner.assembly, coded(ty.extends)?)?;
                }
                Err(format!("Unresolved method {name} at token 0x{token:08x}"))
            }
            _ => Err(format!("Not a method token: 0x{token:08x}")),
        }
    }

    pub fn resolve_field(&self, assembly: usize, token: u32) -> Result<ResolvedField, String> {
        let metadata = self.assembly(assembly)?.metadata();
        if token >> 24 == 4 {
            self.validate_type(
                assembly,
                &self.assembly(assembly)?.field_signature(token)?,
                0,
            )?;
            return Ok(ResolvedField {
                definition: Definition { assembly, token },
                type_arguments: Vec::new(),
                argument_assembly: assembly,
            });
        }
        let reference = row(&metadata.member_refs, token, 0x0a)?;
        let name = metadata
            .strings
            .get(reference.name)
            .map_err(|e| e.to_string())?;
        let signature = crate::signature::field(
            metadata
                .blobs
                .get(reference.signature)
                .map_err(|e| e.to_string())?,
        )?;
        self.validate_type(assembly, &signature, 0)?;
        let (mut owner, type_arguments) = self.parent(assembly, coded(reference.class)?)?;
        for _ in 0..64 {
            let image = self.assembly(owner.assembly)?;
            let owner_metadata = image.metadata();
            let mut found = None;
            let mut signature_bytes = 0usize;
            for &candidate_token in self
                .index(owner.assembly)?
                .fields
                .get(&(owner.token & 0x00ff_ffff))
                .and_then(|names| names.get(name))
                .map(Vec::as_slice)
                .unwrap_or(&[])
            {
                let candidate_row = row(&owner_metadata.fields, candidate_token, 4)?;
                signature_bytes = signature_bytes
                    .checked_add(
                        owner_metadata
                            .blobs
                            .get(candidate_row.signature)
                            .map_err(|e| e.to_string())?
                            .len(),
                    )
                    .ok_or("Signature lookup overflow")?;
                if signature_bytes > 4 * 1024 * 1024 {
                    return Err("Field search exceeds 4 MiB signature budget".into());
                }
                let candidate = image.field_signature(candidate_token)?;
                self.validate_type(owner.assembly, &candidate, 0)?;
                if self.types_equal(assembly, &signature, owner.assembly, &candidate, 0)? {
                    if found
                        .replace(Definition {
                            assembly: owner.assembly,
                            token: candidate_token,
                        })
                        .is_some()
                    {
                        return Err(format!("Ambiguous field {name}"));
                    }
                }
            }
            if let Some(definition) = found {
                return Ok(ResolvedField {
                    definition,
                    type_arguments,
                    argument_assembly: assembly,
                });
            }
            let ty = row(&owner_metadata.type_defs, owner.token, 2)?;
            if null_index(ty.extends, TableId::TypeDef)? {
                break;
            }
            if !type_arguments.is_empty() || ty.extends.table == Some(TableId::TypeSpec) {
                return Err(format!(
                    "{name}: inherited generic substitution requires typed binding"
                ));
            }
            owner = self.resolve_type(owner.assembly, coded(ty.extends)?)?;
        }
        Err(format!("Unresolved field {name} at token 0x{token:08x}"))
    }

    fn methods_equal(
        &self,
        left_assembly: usize,
        left: &MethodSig,
        right_assembly: usize,
        right: &MethodSig,
        depth: usize,
    ) -> Result<bool, String> {
        if left.calling_convention != right.calling_convention
            || left.generic_param_count != right.generic_param_count
            || left.sentinel != right.sentinel
            || left.params.len() != right.params.len()
        {
            return Ok(false);
        }
        if !self.types_equal(
            left_assembly,
            &left.return_type,
            right_assembly,
            &right.return_type,
            depth + 1,
        )? {
            return Ok(false);
        }
        for (left, right) in left.params.iter().zip(&right.params) {
            if !self.types_equal(left_assembly, left, right_assembly, right, depth + 1)? {
                return Ok(false);
            }
        }
        Ok(true)
    }

    fn types_equal(
        &self,
        left_assembly: usize,
        left: &TypeSig,
        right_assembly: usize,
        right: &TypeSig,
        depth: usize,
    ) -> Result<bool, String> {
        if depth >= 64 {
            return Err("Signature comparison nesting exceeds 64".into());
        }
        Ok(match (left, right) {
            (TypeSig::Primitive(a), TypeSig::Primitive(b)) => a == b,
            (TypeSig::Var(a), TypeSig::Var(b)) | (TypeSig::MVar(a), TypeSig::MVar(b)) => a == b,
            (TypeSig::Class(a), TypeSig::Class(b))
            | (TypeSig::ValueType(a), TypeSig::ValueType(b)) => {
                self.resolve_type(left_assembly, type_token(*a)?)?
                    == self.resolve_type(right_assembly, type_token(*b)?)?
            }
            (TypeSig::Ptr(a), TypeSig::Ptr(b))
            | (TypeSig::ByRef(a), TypeSig::ByRef(b))
            | (TypeSig::SzArray(a), TypeSig::SzArray(b))
            | (TypeSig::Pinned(a), TypeSig::Pinned(b)) => {
                self.types_equal(left_assembly, a, right_assembly, b, depth + 1)?
            }
            (
                TypeSig::Array {
                    element_type: a,
                    rank: ar,
                    ..
                },
                TypeSig::Array {
                    element_type: b,
                    rank: br,
                    ..
                },
            ) => ar == br && self.types_equal(left_assembly, a, right_assembly, b, depth + 1)?,
            (
                TypeSig::Modified {
                    required: ar,
                    modifier: am,
                    inner: a,
                },
                TypeSig::Modified {
                    required: br,
                    modifier: bm,
                    inner: b,
                },
            ) => {
                ar == br
                    && self.resolve_type(left_assembly, type_token(*am)?)?
                        == self.resolve_type(right_assembly, type_token(*bm)?)?
                    && self.types_equal(left_assembly, a, right_assembly, b, depth + 1)?
            }
            (
                TypeSig::GenericInst {
                    is_value_type: av,
                    type_ref: a,
                    type_args: aa,
                },
                TypeSig::GenericInst {
                    is_value_type: bv,
                    type_ref: b,
                    type_args: ba,
                },
            ) => {
                if av != bv
                    || aa.len() != ba.len()
                    || self.resolve_type(left_assembly, type_token(*a)?)?
                        != self.resolve_type(right_assembly, type_token(*b)?)?
                {
                    return Ok(false);
                }
                for (a, b) in aa.iter().zip(ba) {
                    if !self.types_equal(left_assembly, a, right_assembly, b, depth + 1)? {
                        return Ok(false);
                    }
                }
                true
            }
            (TypeSig::FnPtr(a), TypeSig::FnPtr(b)) => {
                self.methods_equal(left_assembly, a, right_assembly, b, depth + 1)?
            }
            _ => false,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use rstest::rstest;

    #[rstest]
    #[case(0, Some(true))]
    #[case(1, None)]
    #[case(2, None)]
    #[case(3, None)]
    #[case(4, Some(false))]
    fn only_the_zero_encoding_is_a_null_scope(#[case] raw: u32, #[case] expected: Option<bool>) {
        let index = CodedIndex::decode(clrmeta::CodedIndexKind::ResolutionScope, raw);
        assert_eq!(null_index(index, TableId::Module).ok(), expected);
    }

    #[test]
    fn strong_name_tokens_preserve_identity_bytes() {
        assert_eq!(
            key(b"abc", true).unwrap(),
            Some([0x9d, 0xd8, 0xd0, 0x9c, 0x6c, 0xc2, 0x50, 0x78])
        );
        assert_eq!(key(&[1; 8], false).unwrap(), Some([1; 8]));
        assert!(key(&[1; 7], false).is_err());
        assert!(key(&[], true).is_err());
        assert_eq!(key(&[], false).unwrap(), None);
    }

    #[test]
    fn invalid_entity_ids_report_errors() {
        let resolver = Resolver::new(&[]).unwrap();
        for assembly in [0, usize::MAX] {
            for token in [0, 0x06000000, 0x0600ffff, 0xffffffff] {
                assert!(resolver.resolve_method(assembly, token).is_err());
                assert!(resolver.resolve_field(assembly, token).is_err());
                assert!(resolver.resolve_type(assembly, token).is_err());
            }
        }
    }

    #[test]
    fn array_identity_depends_on_rank_and_element_not_instance_bounds() {
        let resolver = Resolver::new(&[]).unwrap();
        let left = TypeSig::Array {
            element_type: Box::new(TypeSig::Primitive(clrmeta::ElementType::I4)),
            rank: 2,
            sizes: vec![3, 4],
            lo_bounds: vec![0, 0],
        };
        let mut right = TypeSig::Array {
            element_type: Box::new(TypeSig::Primitive(clrmeta::ElementType::I4)),
            rank: 2,
            sizes: vec![9],
            lo_bounds: vec![-7],
        };
        assert!(resolver.types_equal(0, &left, 0, &right, 0).unwrap());
        if let TypeSig::Array { rank, .. } = &mut right {
            *rank = 1;
        }
        assert!(!resolver.types_equal(0, &left, 0, &right, 0).unwrap());
    }
}
