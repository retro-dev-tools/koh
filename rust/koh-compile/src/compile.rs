use crate::{
    ir::{Function, Op, verify_program},
    resolve::{Definition, Resolver},
};
use std::collections::{HashSet, VecDeque};

pub fn object_with_debug(
    resolver: &Resolver<'_>,
    selected: Option<&str>,
    symbols: &[Option<&[u8]>],
) -> Result<koh_objects::Object, String> {
    if !symbols.is_empty() && symbols.len() != resolver.assembly_count() {
        return Err("PDB inputs must correspond to every assembly in resolver order".into());
    }
    let mut input_bytes = 0usize;
    let mut document_bytes = 0usize;
    let mut points = 0usize;
    let mut pdbs = Vec::new();
    for (index, bytes) in symbols.iter().enumerate() {
        let Some(bytes) = bytes else {
            pdbs.push(None);
            continue;
        };
        input_bytes = input_bytes
            .checked_add(bytes.len())
            .ok_or("PDB input size overflow")?;
        if input_bytes > 256 * 1024 * 1024 {
            return Err("Combined PDB input exceeds 256 MiB limit".into());
        }
        let pdb = crate::pdb::PortablePdb::for_assembly(bytes, resolver.assembly(index)?)
            .map_err(|error| format!("Assembly{index} symbols: {error}"))?;
        document_bytes += pdb.documents.iter().map(String::len).sum::<usize>();
        points += pdb.methods.iter().map(Vec::len).sum::<usize>();
        if document_bytes > 16 * 1024 * 1024 || points > 1_000_000 {
            return Err("Combined PDB documents or sequence points exceed limit".into());
        }
        pdbs.push(Some(pdb));
    }
    object_inner(resolver, selected, &pdbs)
}

pub fn object(
    resolver: &Resolver<'_>,
    selected: Option<&str>,
) -> Result<koh_objects::Object, String> {
    object_inner(resolver, selected, &[])
}

fn object_inner(
    resolver: &Resolver<'_>,
    selected: Option<&str>,
    pdbs: &[Option<crate::pdb::PortablePdb>],
) -> Result<koh_objects::Object, String> {
    let entry = entry(resolver, selected)?;
    let interrupts = crate::contracts::interrupts(resolver)?;
    if interrupts.values().any(|&handler| handler == entry) {
        return Err("Interrupt handler cannot be the cartridge entry".into());
    }
    let functions = reachable(resolver, entry)?;
    let interrupts = crate::interrupts::Plan::new(resolver, &functions, entry, interrupts)?;
    let classes = resolver
        .class_layouts
        .borrow()
        .values()
        .map(|layout| {
            if layout.interface {
                crate::ir::ClassType::interface(layout.identity)
            } else {
                crate::ir::ClassType::new(layout.identity, layout.base, layout.bytes)
            }
        })
        .collect::<Result<Vec<_>, _>>()?;
    let interfaces = resolver
        .class_layouts
        .borrow()
        .values()
        .map(|layout| (layout.identity, layout.interfaces.clone()))
        .collect();
    let origins = functions
        .iter()
        .map(|function| {
            Ok((
                function.identity,
                resolver
                    .instance(function.identity)?
                    .map_or(function.identity, |instance| instance.method),
            ))
        })
        .collect::<Result<std::collections::HashMap<_, _>, String>>()?;
    let mut object = crate::cartridge::emit_with_debug(
        &functions,
        entry,
        &classes,
        &interfaces,
        pdbs,
        &origins,
        &interrupts,
    )?;
    let mut names = std::collections::HashMap::new();
    let mut name_bytes = 0usize;
    for function in &functions {
        let id = origins[&function.identity];
        if id.token >> 24 != 6 {
            continue;
        }
        let key = crate::sm83::symbol(function.identity);
        let name = format!(
            "{} [{key}]",
            resolver.assembly(id.assembly)?.method_name(id.token)?
        );
        if name.len() > 65_535 {
            return Err("Debug function name exceeds 65535 bytes".into());
        }
        let source = pdbs
            .get(id.assembly)
            .and_then(Option::as_ref)
            .and_then(|pdb| {
                pdb.methods
                    .get((id.token & 0xffffff).checked_sub(1)? as usize)?
                    .iter()
                    .find_map(|point| {
                        let mut source = pdb.source(id.token, point.il_offset)?;
                        if source.file.is_empty() || source.line == 0 {
                            return None;
                        }
                        source.column = source.column.max(1);
                        Some(source)
                    })
            });
        name_bytes += name.len() + source.as_ref().map_or(0, |p| p.file.len()) + 128;
        if name_bytes > 64 * 1024 * 1024 {
            return Err("Debug function names exceed 64 MiB limit".into());
        }
        names.insert(key, (name, source));
    }
    let aliases: Vec<_> = object
        .symbols
        .iter()
        .filter_map(|symbol| {
            let (name, source) = names.get(&symbol.name)?;
            if !matches!(
                symbol.definition,
                koh_objects::SymbolDefinition::Label { .. }
            ) {
                return None;
            }
            Some(koh_objects::Symbol {
                name: name.clone(),
                source: source.clone(),
                binding: koh_objects::SymbolBinding::Local,
                definition: symbol.definition.clone(),
            })
        })
        .collect();
    object.symbols.extend(aliases);
    object.validate()?;
    Ok(object)
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
    resolver.generic_instances.borrow_mut().clear();
    resolver.type_instances.borrow_mut().clear();
    resolver.field_instances.borrow_mut().clear();
    resolver.delegate_types.borrow_mut().clear();
    resolver.delegate_type_work.set(0);
    resolver.value_layouts.borrow_mut().clear();
    resolver.class_layouts.borrow_mut().clear();
    resolver.class_layout_work.set(0);
    resolver.reference_layout_work.set(0);
    resolver.callbacks.borrow_mut().clear();
    resolver.generic_work.set(0);
    resolver.generic_nodes.set(0);
    let mut seen = HashSet::from([entry]);
    let mut queue = VecDeque::from([entry]);
    let mut functions = Vec::new();
    let mut instructions = 0usize;
    let mut data_bytes = 0usize;
    let mut virtuals = std::collections::BTreeMap::new();
    let mut classes = std::collections::BTreeMap::new();
    let mut binder = crate::dispatch::Binder::new(resolver);
    for assembly in 0..resolver.assembly_count() {
        crate::contracts::assembly(resolver, assembly)?;
    }
    let interrupts = crate::contracts::interrupts(resolver)?;
    for &handler in interrupts.values() {
        if seen.insert(handler) {
            queue.push_back(handler);
        }
    }
    loop {
        while let Some(identity) = queue.pop_front() {
            let function = if identity.token >> 24 == 0xfc {
                crate::delegates::function(resolver, identity)?
            } else if matches!(identity.token >> 24, 0xf7 | 0xff) {
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
                    if let Op::NewObject(class) = instruction.op {
                        if let Some(id) = class.identity() {
                            classes
                                .entry((id.assembly, id.token))
                                .or_insert((class, (identity, instruction.il_offset)));
                        }
                    }
                    if let Op::InitializeArray { ref bytes, .. } = instruction.op {
                        data_bytes += bytes.len();
                        if data_bytes > 16 * 1024 * 1024 {
                            return Err("Reachable initializer data exceeds 16 MiB budget".into());
                        }
                    }
                    if let Op::StringLiteral(ref units) = instruction.op {
                        data_bytes += units.len() * 2 + 2;
                        if data_bytes > 16 * 1024 * 1024 {
                            return Err("Reachable literal data exceeds 16 MiB budget".into());
                        }
                    }
                    if let Op::Call { target, .. } | Op::NewDelegate { target, .. } = instruction.op
                    {
                        if interrupts.values().any(|&handler| handler == target) {
                            return Err(format!(
                                "Interrupt handler cannot be called or captured: {identity:?} IL_{:04x}",
                                instruction.il_offset
                            ));
                        }
                        if seen.insert(target) {
                            if seen.len() > 4096 {
                                return Err("Reachable method count exceeds 4096".into());
                            }
                            if crate::dispatch::is_dispatch(target) {
                                virtuals.insert(
                                    (target.assembly, target.token),
                                    (
                                        crate::dispatch::declaration(target),
                                        format!(
                                            "{} IL_{:04x}",
                                            if identity.token >> 24 == 0xfc {
                                                format!("Callback {:?}", identity)
                                            } else {
                                                let origin = resolver
                                                    .instance(identity)?
                                                    .map_or(identity, |instance| instance.method);
                                                resolver
                                                    .assembly(origin.assembly)?
                                                    .method_name(origin.token)?
                                            },
                                            instruction.il_offset
                                        ),
                                    ),
                                );
                            } else {
                                queue.push_back(target);
                            }
                        }
                    }
                }
            }
            functions.push(function);
        }
        for (method, site) in virtuals.values() {
            for &(class, _) in classes.values() {
                if let Some(target) = binder
                    .implementation(*method, class)
                    .map_err(|error| format!("{site}: {error}"))?
                {
                    if seen.insert(target) {
                        if seen.len() > 4096 {
                            return Err("Reachable method count exceeds 4096".into());
                        }
                        queue.push_back(target);
                    }
                }
            }
        }
        if queue.is_empty() {
            break;
        }
    }
    let mut allocated_classes = Vec::new();
    for (class, (site, offset)) in classes.into_values() {
        let origin = resolver
            .instance(site)?
            .map_or(site, |instance| instance.method);
        let name = if origin.token >> 24 == 6 {
            resolver
                .assembly(origin.assembly)?
                .method_name(origin.token)?
        } else {
            format!("Generated function {origin:?}")
        };
        binder.validate_interfaces(class).map_err(|error| {
            format!(
                "{name} IL_{offset:04x}: Allocated class {:?}: {error}",
                class.identity()
            )
        })?;
        allocated_classes.push(class);
    }
    let classes = allocated_classes;
    for (method, site) in virtuals.into_values() {
        let function = binder
            .function(method, &classes)
            .map_err(|error| format!("{site}: {error}"))?;
        instructions += function
            .blocks
            .iter()
            .map(|b| b.parameters.len() + b.instructions.len() + 1)
            .sum::<usize>();
        if instructions > 2_000_000 {
            return Err("Reachable IR exceeds program size limit".into());
        }
        functions.push(function);
    }
    functions.sort_by_key(|f| (f.identity.assembly, f.identity.token));
    verify_program(&functions)?;
    for function in &mut functions {
        crate::optimize::simplify(function)?;
        crate::alignment::apply(resolver, function)?;
    }
    crate::optimize::constant_calls(&mut functions)?;
    verify_program(&functions)?;
    Ok(functions)
}
