use crate::ir::*;
use std::collections::{BTreeMap, HashMap, HashSet};

fn pure(op: &Op) -> bool {
    matches!(
        op,
        Op::Constant(_)
            | Op::ZeroStruct
            | Op::FieldHandle(_)
            | Op::StringLiteral(_)
            | Op::LoadLocal(_)
            | Op::Convert { .. }
            | Op::Compare { .. }
            | Op::Negate(_)
            | Op::Not(_)
    ) || matches!(op, Op::Binary { op, .. } if !matches!(op,
            Binary::DivideSigned | Binary::DivideUnsigned | Binary::RemainderSigned | Binary::RemainderUnsigned))
}

fn remove_unused(function: &mut Function) {
    let mut definitions = BTreeMap::new();
    let mut pending = Vec::new();
    for block in &function.blocks {
        for instruction in &block.instructions {
            if let Some((id, _)) = instruction.result.filter(|_| pure(&instruction.op)) {
                definitions.insert(id, instruction.op.operands().collect::<Vec<_>>());
            } else {
                pending.extend(instruction.op.operands());
            }
        }
        for edge in block.terminator.kind.edges() {
            pending.extend(&edge.arguments);
        }
        match block.terminator.kind {
            Control::Return(Some(id))
            | Control::Branch { condition: id, .. }
            | Control::Switch { selector: id, .. } => pending.push(id),
            _ => {}
        }
    }
    let mut used = HashSet::new();
    while let Some(id) = pending.pop() {
        if used.insert(id) {
            if let Some(operands) = definitions.get(&id) {
                pending.extend(operands);
            }
        }
    }
    for block in &mut function.blocks {
        block
            .instructions
            .retain(|i| !pure(&i.op) || i.result.is_none_or(|(id, _)| used.contains(&id)));
    }
}

pub fn simplify(function: &mut Function) -> Result<(), String> {
    verify(function)?;
    let mut predecessors = vec![0usize; function.blocks.len()];
    for block in &function.blocks {
        for edge in block.terminator.kind.edges() {
            predecessors[edge.block] += 1;
        }
    }
    let mut blocks: Vec<_> = std::mem::take(&mut function.blocks)
        .into_iter()
        .map(Some)
        .collect();
    let mut owner: Vec<_> = (0..blocks.len()).collect();
    let mut aliases = BTreeMap::new();
    for index in 0..blocks.len() {
        let Some(mut block) = blocks[index].take() else {
            continue;
        };
        while let Control::Jump(edge) = &block.terminator.kind {
            let target = edge.block;
            if target == 0 || target == index || predecessors[target] != 1 {
                break;
            }
            let Some(next) = blocks[target].take() else {
                break;
            };
            for (&(parameter, _), &argument) in next.parameters.iter().zip(&edge.arguments) {
                aliases.insert(parameter, argument);
            }
            owner[target] = index;
            block.instructions.extend(next.instructions);
            block.terminator = next.terminator;
        }
        blocks[index] = Some(block);
    }
    for block in blocks.iter().flatten() {
        let mut locals = BTreeMap::new();
        let mut constants = HashMap::new();
        for instruction in &block.instructions {
            match instruction.op {
                Op::Constant(bits) => {
                    if let Some((id, ty)) = instruction.result {
                        if let Some(&value) = constants.get(&(ty, bits)) {
                            aliases.insert(id, value);
                        } else {
                            constants.insert((ty, bits), id);
                        }
                    }
                }
                Op::StoreLocal { local, value } => {
                    locals.insert(local, value);
                }
                Op::LoadLocal(local) => {
                    if let Some((id, _)) = instruction.result {
                        if let Some(&value) = locals.get(&local) {
                            aliases.insert(id, value);
                        } else {
                            locals.insert(local, id);
                        }
                    }
                }
                _ if pure(&instruction.op) => {}
                _ => locals.clear(),
            }
        }
    }
    let resolve = |mut value| -> Result<u32, String> {
        for _ in 0..=aliases.len() {
            let Some(&next) = aliases.get(&value) else {
                return Ok(value);
            };
            value = next;
        }
        Err("Cyclic value aliases during CFG simplification".into())
    };
    let mut resolved = BTreeMap::new();
    for &value in aliases.keys() {
        resolved.insert(value, resolve(value)?);
    }
    let value = |id: &mut u32| {
        *id = resolved.get(id).copied().unwrap_or(*id);
    };
    let mut remap = vec![0; blocks.len()];
    let mut count = 0;
    for (index, block) in blocks.iter().enumerate() {
        if block.is_some() {
            remap[index] = count;
            count += 1;
        }
    }
    for index in 0..owner.len() {
        let mut root = index;
        while owner[root] != root {
            root = owner[root];
        }
        remap[index] = remap[root];
    }
    for mut block in blocks.into_iter().flatten() {
        for instruction in &mut block.instructions {
            match &mut instruction.op {
                Op::StackAllocate { bytes } => value(bytes),
                Op::InitializeArray { array, .. } => value(array),
                Op::NewArray { length, .. }
                | Op::ArrayLength(length)
                | Op::StringLength(length) => value(length),
                Op::StringChar { string, index } => {
                    value(string);
                    value(index);
                }
                Op::ArrayAddress { array, index } => {
                    value(array);
                    value(index);
                }
                Op::LoadMemory { address, .. } | Op::FieldAddress { address, .. } => value(address),
                Op::ExtractField {
                    value: extracted, ..
                } => value(extracted),
                Op::StoreMemory {
                    address,
                    value: stored,
                    ..
                } => {
                    value(address);
                    value(stored);
                }
                Op::Binary { left, right, .. } | Op::Compare { left, right, .. } => {
                    value(left);
                    value(right);
                }
                Op::Negate(id)
                | Op::Not(id)
                | Op::Convert { value: id, .. }
                | Op::StoreLocal { value: id, .. }
                | Op::StorePinned { value: id, .. } => value(id),
                Op::Call { arguments, .. } => {
                    for id in arguments {
                        value(id);
                    }
                }
                Op::Constant(_)
                | Op::ZeroStruct
                | Op::FieldHandle(_)
                | Op::LocalAddress { .. }
                | Op::StaticAddress { .. }
                | Op::InitializerAddress(_)
                | Op::LoadLocal(_)
                | Op::StringLiteral(_) => {}
            }
        }
        let edge = |edge: &mut Edge| {
            edge.block = remap[edge.block];
            for id in &mut edge.arguments {
                value(id);
            }
        };
        match &mut block.terminator.kind {
            Control::Branch { condition, .. } => {
                value(condition);
            }
            Control::Switch { selector, .. } => value(selector),
            Control::Jump(_) => {}
            Control::Return(result) => {
                if let Some(id) = result {
                    value(id);
                }
            }
        }
        for jump in block.terminator.kind.edges_mut() {
            edge(jump);
        }
        function.blocks.push(block);
    }
    remove_unused(function);
    let read: HashSet<_> = function
        .blocks
        .iter()
        .flat_map(|b| &b.instructions)
        .filter_map(|i| match i.op {
            Op::LoadLocal(local)
            | Op::LocalAddress { local, .. }
            | Op::StorePinned { local, .. } => Some(local),
            _ => None,
        })
        .collect();
    for block in &mut function.blocks {
        block
            .instructions
            .retain(|i| !matches!(i.op, Op::StoreLocal { local, .. } if !read.contains(&local)));
    }
    remove_unused(function);
    verify(function)
}
