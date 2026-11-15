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
        let constants: HashMap<_, _> = function
            .blocks
            .iter()
            .flat_map(|b| &b.instructions)
            .filter_map(|i| match (i.result, &i.op) {
                (Some((id, _)), Op::Constant(bits)) => Some((id, *bits)),
                _ => None,
            })
            .collect();
        for i in function.blocks.iter().flat_map(|b| &b.instructions) {
            if let Op::Call { target, .. } = i.op {
                callers.entry(target).or_default().push(function.identity);
            }
            if matches!(i.op, Op::StoreMemory { address, .. } if constants.get(&address) == Some(&0xff70))
                && writes.insert(function.identity)
            {
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
