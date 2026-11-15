use super::{Function, Op};
use std::collections::{HashMap, HashSet, VecDeque};

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
    let mut writes = HashSet::new();
    let mut pending = VecDeque::new();
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
            let bank_write = match i.op {
                Op::StoreMemory { address, .. } => constants.get(&address) == Some(&0xff70),
                Op::MemoryFill { address, count, .. } => constants
                    .get(&address)
                    .zip(constants.get(&count))
                    .is_some_and(|(&address, &count)| {
                        0xff70u16.wrapping_sub(address as u16) < count as u16
                    }),
                _ => false,
            };
            if bank_write && writes.insert(function.identity) {
                pending.push_back(function.identity);
            }
        }
    }
    while let Some(method) = pending.pop_front() {
        for &caller in callers.get(&method).into_iter().flatten() {
            if writes.insert(caller) {
                pending.push_back(caller);
            }
        }
    }
    for function in functions {
        if !writes.contains(&function.identity) {
            continue;
        }
        if let Some(i) = function
            .blocks
            .iter()
            .flat_map(|b| &b.instructions)
            .find(|i| matches!(i.op, Op::StorePinned { .. }))
        {
            return Err(format!(
                "Assembly {} method 0x{:08x} IL_{:04x}: Explicit WRAM bank writes reachable from a pinned method are unsupported; select the bank in its caller",
                function.identity.assembly, function.identity.token, i.il_offset
            ));
        }
    }
    Ok(())
}
