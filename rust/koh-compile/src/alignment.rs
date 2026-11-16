use crate::{
    contracts,
    ir::{Function, Op, Type},
    resolve::Resolver,
};
use std::collections::{BTreeMap, HashMap, HashSet};

enum Value<'a> {
    Operation(&'a Op),
    Parameter { block: usize, slot: usize },
}

pub(crate) fn apply(resolver: &Resolver<'_>, function: &mut Function) -> Result<(), String> {
    let mut values = HashMap::new();
    let mut types = HashMap::new();
    let mut stores: HashMap<u32, Vec<u32>> = HashMap::new();
    let mut incoming = vec![vec![]; function.blocks.len()];
    for (block, body) in function.blocks.iter().enumerate() {
        for (slot, &(id, ty)) in body.parameters.iter().enumerate() {
            values.insert(id, Value::Parameter { block, slot });
            types.insert(id, ty);
        }
        for i in &body.instructions {
            if let Some((id, ty)) = i.result {
                values.insert(id, Value::Operation(&i.op));
                types.insert(id, ty);
            }
            if let Op::StoreLocal { local, value } | Op::StorePinned { local, value } = i.op {
                stores.entry(local).or_default().push(value);
            }
        }
        for edge in body.terminator.kind.edges() {
            incoming[edge.block].push(edge);
        }
    }
    let mut constraints: BTreeMap<u32, u16> = BTreeMap::new();
    let mut work = 0usize;
    for i in function.blocks.iter().flat_map(|b| &b.instructions) {
        let Op::StoreMemory { address, value, .. } = i.op else {
            continue;
        };
        if !matches!(types[&value], Type::Array(_, _)) {
            continue;
        }
        let Some(Value::Operation(Op::StaticAddress { field, .. })) = values.get(&address) else {
            continue;
        };
        let alignment = contracts::field_alignment(resolver, *field)?;
        if alignment == 1 {
            continue;
        }
        let mut checked = || -> Result<HashSet<u32>, String> {
            let mut pending = vec![value];
            let mut seen = HashSet::new();
            let mut allocations = HashSet::new();
            while let Some(id) = pending.pop() {
                work += 1;
                if work > 1_048_576 {
                    return Err("Array alignment dependency limit exceeded".into());
                }
                if !seen.insert(id) {
                    continue;
                }
                match values.get(&id).ok_or("Missing alignment dependency")? {
                    Value::Operation(Op::NewArray {..}) => { allocations.insert(id); },
                    Value::Operation(Op::Constant(0)) => {},
                    Value::Operation(Op::LoadLocal(local)) => {
                        pending.extend(stores.get(local).ok_or("Uninitialized aligned array local")?);
                    },
                    Value::Parameter {block,slot} if *block != 0 => {
                        pending.extend(incoming[*block].iter().map(|edge| edge.arguments[*slot]));
                    },
                    _ => return Err("Aligned array assignment requires a visible newarr in the assigning method; factory calls and external aliases are unsupported".into()),
                }
                if pending.len() > 1_048_576 {
                    return Err("Array alignment dependency limit exceeded".into());
                }
            }
            Ok(allocations)
        };
        let method_name = resolver
            .assembly(function.identity.assembly)?
            .method_name(function.identity.token)?;
        let allocations = checked().map_err(|error| {
            format!(
                "{method_name} IL_{:04x}: aligned field 0x{:08x}: {error}",
                i.il_offset, field.token
            )
        })?;
        for id in allocations {
            constraints
                .entry(id)
                .and_modify(|old| *old = (*old).max(alignment))
                .or_insert(alignment);
        }
    }
    for i in function.blocks.iter_mut().flat_map(|b| &mut b.instructions) {
        if let (Some((id, _)), Op::NewArray { alignment, .. }) = (i.result, &mut i.op) {
            *alignment = (*alignment).max(constraints.get(&id).copied().unwrap_or(1));
        }
    }
    Ok(())
}
