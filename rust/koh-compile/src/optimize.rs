use crate::ir::*;
use std::collections::BTreeMap;

pub fn simplify(function: &mut Function) -> Result<(), String> {
    verify(function)?;
    let mut predecessors = vec![0usize; function.blocks.len()];
    for block in &function.blocks {
        match &block.terminator.kind {
            Control::Jump(edge) => predecessors[edge.block] += 1,
            Control::Branch { yes, no, .. } => {
                predecessors[yes.block] += 1;
                predecessors[no.block] += 1;
            }
            Control::Return(_) => {}
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
                Op::LoadMemory { address, .. } => value(address),
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
                | Op::StoreLocal { value: id, .. } => value(id),
                Op::Call { arguments, .. } => {
                    for id in arguments {
                        value(id);
                    }
                }
                Op::Constant(_) | Op::StaticAddress { .. } | Op::LoadLocal(_) => {}
            }
        }
        let edge = |edge: &mut Edge| {
            edge.block = remap[edge.block];
            for id in &mut edge.arguments {
                value(id);
            }
        };
        match &mut block.terminator.kind {
            Control::Jump(jump) => edge(jump),
            Control::Branch { condition, yes, no } => {
                value(condition);
                edge(yes);
                edge(no);
            }
            Control::Return(result) => {
                if let Some(id) = result {
                    value(id);
                }
            }
        }
        function.blocks.push(block);
    }
    verify(function)
}
