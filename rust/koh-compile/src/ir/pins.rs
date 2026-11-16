use super::{Function, Op, Type};
use std::collections::{HashMap, HashSet, VecDeque};

fn bank_write(op: &Op, constants: &HashMap<u32, u64>) -> bool {
    match *op {
        Op::StoreMemory { address, .. } => constants.get(&address) == Some(&0xff70),
        Op::MemoryFill { address, count, .. }
        | Op::MemoryCopy {
            destination: address,
            count,
            ..
        }
        | Op::VBlankCopy {
            destination: address,
            count,
            ..
        } => constants
            .get(&address)
            .zip(constants.get(&count))
            .is_some_and(|(&address, &count)| {
                0xff70u16.wrapping_sub(address as u16) < count as u16
            }),
        _ => false,
    }
}

pub(super) fn verify_banks(functions: &[Function]) -> Result<(), String> {
    if !functions
        .iter()
        .flat_map(|f| &f.blocks)
        .flat_map(|b| &b.instructions)
        .any(|i| matches!(i.op, Op::StorePinned { .. }))
    {
        return Ok(());
    }
    let mut callers: HashMap<_, Vec<_>> = HashMap::new();
    let mut delegate_targets = HashMap::<_, HashSet<_>>::new();
    for instruction in functions
        .iter()
        .flat_map(|function| &function.blocks)
        .flat_map(|block| &block.instructions)
    {
        if let Op::NewDelegate { kind, target, .. } = instruction.op {
            delegate_targets.entry(kind).or_default().insert(target);
        }
    }
    let mut indirect_edges = 0usize;
    let mut writes = HashSet::new();
    let mut pending = VecDeque::new();
    let mut constants_by_function = Vec::with_capacity(functions.len());
    for function in functions {
        let mut constants: HashMap<_, _> = function
            .blocks
            .iter()
            .flat_map(|b| &b.instructions)
            .filter_map(|i| match (i.result, &i.op) {
                (Some((id, _)), Op::Constant(bits)) => Some((id, *bits)),
                _ => None,
            })
            .collect();
        let types: HashMap<_, _> = function
            .blocks
            .iter()
            .flat_map(|b| {
                b.parameters
                    .iter()
                    .copied()
                    .chain(b.instructions.iter().filter_map(|i| i.result))
            })
            .collect();
        let mut conversions = HashMap::<u32, Vec<(u32, u16, u16, bool)>>::new();
        for i in function.blocks.iter().flat_map(|b| &b.instructions) {
            if let (
                Some((id, ty)),
                Op::Convert {
                    value,
                    bits,
                    signed,
                },
            ) = (i.result, &i.op)
            {
                let copied = u16::from(*bits).min(types[value].bits()).min(ty.bits());
                conversions
                    .entry(*value)
                    .or_default()
                    .push((id, ty.bits(), copied, *signed));
            }
        }
        let mask = |bits: u16| {
            if bits == 64 {
                u64::MAX
            } else {
                (1u64 << bits) - 1
            }
        };
        let mut known: VecDeque<_> = constants.keys().copied().collect();
        while let Some(input) = known.pop_front() {
            for &(id, target, copied, signed) in conversions.get(&input).into_iter().flatten() {
                let mut value = constants[&input] & mask(copied);
                if signed && value & (1u64 << (copied - 1)) != 0 {
                    value |= !mask(copied);
                }
                if constants.insert(id, value & mask(target)).is_none() {
                    known.push_back(id);
                }
            }
        }
        for i in function.blocks.iter().flat_map(|b| &b.instructions) {
            if let Op::Call { target, .. } = i.op {
                callers.entry(target).or_default().push(function.identity);
            }
            if let Op::InvokeDelegate { ref signature, .. } = i.op {
                if let Some(Type::Delegate(kind)) = signature.parameters.first() {
                    for &target in delegate_targets.get(kind).into_iter().flatten() {
                        indirect_edges += 1;
                        if indirect_edges > 1_048_576 {
                            return Err("Delegate bank dependencies exceed1048576".into());
                        }
                        callers.entry(target).or_default().push(function.identity);
                    }
                }
            }
            if bank_write(&i.op, &constants) && writes.insert(function.identity) {
                pending.push_back(function.identity);
            }
        }
        constants_by_function.push(constants);
    }
    while let Some(method) = pending.pop_front() {
        for &caller in callers.get(&method).into_iter().flatten() {
            if writes.insert(caller) {
                pending.push_back(caller);
            }
        }
    }
    for (function, constants) in functions.iter().zip(&constants_by_function) {
        if !writes.contains(&function.identity) {
            continue;
        }
        let mut incoming = vec![HashSet::new(); function.blocks.len()];
        let mut visited = vec![false; function.blocks.len()];
        visited[0] = true;
        let mut pending = VecDeque::from([0]);
        while let Some(index) = pending.pop_front() {
            let mut active = incoming[index].clone();
            let block = &function.blocks[index];
            for i in &block.instructions {
                if let Op::StorePinned { local, value } = i.op {
                    if constants.get(&value) == Some(&0) {
                        active.remove(&local);
                    } else {
                        active.insert(local);
                    }
                }
                let writes_bank = bank_write(&i.op, constants)
                    || matches!(i.op, Op::Call { target, .. } if writes.contains(&target))
                    || match &i.op {
                        Op::InvokeDelegate { signature, .. } => {
                            match signature.parameters.first() {
                                Some(Type::Delegate(kind)) => delegate_targets
                                    .get(kind)
                                    .into_iter()
                                    .flatten()
                                    .any(|target| writes.contains(target)),
                                _ => false,
                            }
                        }
                        _ => false,
                    };
                if writes_bank && !active.is_empty() {
                    return Err(format!(
                        "Assembly {} method 0x{:08x} IL_{:04x}: Explicit WRAM bank writes reachable from a pinned method while a pin may be active are unsupported; select the bank outside its pinned scopes",
                        function.identity.assembly, function.identity.token, i.il_offset
                    ));
                }
            }
            for edge in block.terminator.kind.edges() {
                let before = incoming[edge.block].len();
                incoming[edge.block].extend(&active);
                if !visited[edge.block] || incoming[edge.block].len() != before {
                    visited[edge.block] = true;
                    pending.push_back(edge.block);
                }
            }
        }
    }
    Ok(())
}
