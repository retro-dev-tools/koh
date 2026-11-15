use crate::{
    ir::{Function, Op, verify_program},
    resolve::{Definition, Resolver},
};
use std::collections::{HashSet, VecDeque};

pub fn object(
    resolver: &Resolver<'_>,
    selected: Option<&str>,
) -> Result<koh_objects::Object, String> {
    let entry = entry(resolver, selected)?;
    let functions = reachable(resolver, entry)?;
    crate::cartridge::emit(&functions, entry)
}

pub fn entry(resolver: &Resolver<'_>, selected: Option<&str>) -> Result<Definition, String> {
    let assembly = resolver.assembly(0)?;
    let metadata = assembly.metadata();
    let token = if selected.is_none() && assembly.entry_point != 0 {
        assembly.entry_point
    } else {
        let selection = selected
            .map(|s| s.rsplit_once("::").ok_or("Entry must be Type::Method"))
            .transpose()?;
        let mut candidates = Vec::new();
        let mut parents = vec![0; metadata.type_defs.len()];
        for nested in &metadata.nested_classes {
            parents[nested.nested_class as usize - 1] = nested.enclosing_class;
        }
        for (index, ty) in metadata.type_defs.iter().enumerate() {
            if let Some((owner, _)) = selection {
                let qualified = type_name(metadata, &parents, index)?;
                if qualified != owner {
                    continue;
                }
            }
            let end = metadata
                .type_defs
                .get(index + 1)
                .map(|t| t.method_list)
                .unwrap_or(metadata.method_defs.len() as u32 + 1);
            for row in ty.method_list..end {
                let method = &metadata.method_defs[row as usize - 1];
                let name = metadata
                    .strings
                    .get(method.name)
                    .map_err(|e| e.to_string())?;
                if method.flags & 0x10 != 0 && name == selection.map(|s| s.1).unwrap_or("Main") {
                    candidates.push(0x06000000 | row);
                    if candidates.len() > 1 {
                        return Err(
                            "Entry is ambiguous; select a unique Type::Method with --entry".into(),
                        );
                    }
                }
            }
        }
        *candidates
            .first()
            .ok_or("No static entry method found (assembly entry or Main)")?
    };
    let signature = assembly.method_signature(token)?;
    if matches!(
        metadata
            .strings
            .get(assembly.method(token)?.name)
            .map_err(|e| e.to_string())?,
        ".ctor" | ".cctor"
    ) {
        return Err("Constructors cannot be cartridge entry methods".into());
    }
    let owner = metadata
        .type_defs
        .partition_point(|ty| ty.method_list <= token & 0x00ff_ffff) as u32;
    if signature.calling_convention.has_this()
        || signature.generic_param_count != 0
        || metadata
            .generic_params
            .iter()
            .any(|p| p.owner.table == Some(clrmeta::TableId::TypeDef) && p.owner.row == owner)
    {
        return Err("Entry must be static and non-generic".into());
    }
    if !signature.params.is_empty() {
        return Err("Entry must have no parameters".into());
    }
    if !matches!(
        signature.return_type,
        clrmeta::TypeSig::Primitive(
            clrmeta::ElementType::Void | clrmeta::ElementType::I4 | clrmeta::ElementType::U4
        )
    ) {
        return Err("Entry must return void or int32".into());
    }
    Ok(Definition { assembly: 0, token })
}

fn type_name(
    metadata: &clrmeta::Metadata,
    parents: &[u32],
    mut index: usize,
) -> Result<String, String> {
    let mut names = Vec::new();
    let namespace = loop {
        if names.len() >= 64 {
            return Err("Entry type nesting exceeds limit".into());
        }
        let ty = &metadata.type_defs[index];
        names.push(
            metadata
                .strings
                .get(ty.type_name)
                .map_err(|e| e.to_string())?,
        );
        if parents[index] == 0 {
            break metadata
                .strings
                .get(ty.type_namespace)
                .map_err(|e| e.to_string())?;
        }
        index = parents[index] as usize - 1;
    };
    names.reverse();
    let name = names.join("+");
    Ok(if namespace.is_empty() {
        name
    } else {
        format!("{namespace}.{name}")
    })
}

pub fn reachable(resolver: &Resolver<'_>, entry: Definition) -> Result<Vec<Function>, String> {
    let mut seen = HashSet::from([entry]);
    let mut queue = VecDeque::from([entry]);
    let mut functions = Vec::new();
    let mut instructions = 0usize;
    let mut data_bytes = 0usize;
    for assembly in 0..resolver.assembly_count() {
        crate::contracts::assembly(resolver, assembly)?;
    }
    while let Some(identity) = queue.pop_front() {
        let function = if identity.token >> 24 == 0xff {
            crate::initialize::function(resolver, identity)?
        } else {
            crate::lower::assembly_method(resolver, identity)?
        };
        instructions += function
            .blocks
            .iter()
            .map(|b| b.parameters.len() + b.instructions.len() + 1)
            .sum::<usize>();
        if instructions > 2_000_000 {
            return Err("Reachable IR exceeds program size limit".into());
        }
        for block in &function.blocks {
            for instruction in &block.instructions {
                if let Op::InitializeArray { ref bytes, .. } = instruction.op {
                    data_bytes += bytes.len();
                    if data_bytes > 16 * 1024 * 1024 {
                        return Err("Reachable initializer data exceeds 16 MiB budget".into());
                    }
                }
                if let Op::Call { target, .. } = instruction.op {
                    if seen.insert(target) {
                        if seen.len() > 4096 {
                            return Err("Reachable method count exceeds 4096".into());
                        }
                        queue.push_back(target);
                    }
                }
            }
        }
        functions.push(function);
    }
    functions.sort_by_key(|f| (f.identity.assembly, f.identity.token));
    verify_program(&functions)?;
    for function in &mut functions {
        crate::optimize::simplify(function)?;
    }
    verify_program(&functions)?;
    Ok(functions)
}
