use super::{Control, Function, Op};
use std::collections::{HashMap, HashSet, VecDeque};

pub(super) fn verify(function: &Function) -> Result<(), String> {
    if function.returns.is_none_or(|ty| !ty.is_byref()) {
        return Ok(());
    }
    let mut pending = VecDeque::new();
    for instruction in function.blocks.iter().flat_map(|b| &b.instructions) {
        if matches!(instruction.op, Op::LocalAddress { .. }) {
            if let Some((id, _)) = instruction.result {
                pending.push_back(u64::from(id));
            }
        }
    }
    if pending.is_empty() {
        return Ok(());
    }
    let references: HashSet<_> = function
        .blocks
        .iter()
        .flat_map(|block| {
            block
                .parameters
                .iter()
                .copied()
                .chain(block.instructions.iter().filter_map(|i| i.result))
        })
        .filter(|(_, ty)| ty.is_byref())
        .map(|(id, _)| id)
        .collect();
    let local = |index: u32| (1u64 << 32) | u64::from(index);
    let mut graph: HashMap<u64, Vec<u64>> = HashMap::new();
    let mut count = 0usize;
    let mut edge = |from, to| -> Result<(), String> {
        count += 1;
        if count > 1_048_576 {
            return Err("Managed reference dependency limit exceeded".into());
        }
        graph.entry(from).or_default().push(to);
        Ok(())
    };
    for block in &function.blocks {
        for instruction in &block.instructions {
            if let Op::StoreLocal {
                local: index,
                value,
            }
            | Op::StorePinned {
                local: index,
                value,
            } = instruction.op
            {
                if references.contains(&value) {
                    edge(u64::from(value), local(index))?;
                }
            }
            if let Some((id, _)) = instruction.result.filter(|(_, ty)| ty.is_byref()) {
                match &instruction.op {
                    Op::FieldAddress { address, .. } => edge(u64::from(*address), u64::from(id))?,
                    Op::LoadLocal(index) => edge(local(*index), u64::from(id))?,
                    Op::Call { arguments, .. } | Op::InvokeDelegate { arguments, .. } => {
                        for argument in arguments {
                            if references.contains(argument) {
                                edge(u64::from(*argument), u64::from(id))?;
                            }
                        }
                    }
                    _ => {}
                }
            }
        }
        for successor in block.terminator.kind.edges() {
            for (&argument, &(parameter, _)) in successor
                .arguments
                .iter()
                .zip(&function.blocks[successor.block].parameters)
            {
                if references.contains(&parameter) {
                    edge(u64::from(argument), u64::from(parameter))?;
                }
            }
        }
    }
    let mut frame = HashSet::new();
    while let Some(id) = pending.pop_front() {
        if frame.insert(id) {
            if let Some(uses) = graph.get(&id) {
                pending.extend(uses);
            }
        }
    }
    for block in &function.blocks {
        if let Control::Return(Some(id)) = block.terminator.kind {
            if frame.contains(&u64::from(id)) {
                return Err(format!(
                    "IL_{:04x}: reference to current frame may escape; scoped return contracts are unsupported",
                    block.terminator.il_offset
                ));
            }
        }
    }
    Ok(())
}
