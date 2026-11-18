use super::{Function, Op};
use std::collections::VecDeque;

pub(super) fn incoming(
    function: &Function,
    predecessors: &[Vec<usize>],
) -> Result<Vec<Vec<u64>>, String> {
    let count = function.locals.len();
    let words = count.div_ceil(64);
    let mut full = vec![u64::MAX; words];
    if !count.is_multiple_of(64) {
        full[words - 1] = (1u64 << (count % 64)) - 1;
    }
    let mut writes = vec![vec![0u64; words]; function.blocks.len()];
    for (block, body) in function.blocks.iter().enumerate() {
        for instruction in &body.instructions {
            if let Op::StoreLocal { local, .. } = instruction.op {
                let local = local as usize;
                if local >= count {
                    return Err("Invalid local index".into());
                }
                writes[block][local / 64] |= 1u64 << (local % 64);
            }
        }
    }
    let mut incoming = vec![full.clone(); function.blocks.len()];
    incoming[0].fill(0);
    if writes[0] == full {
        return Ok(incoming);
    }
    let mut outgoing = vec![full.clone(); function.blocks.len()];
    let mut queue: VecDeque<_> = (0..function.blocks.len()).collect();
    let mut queued = vec![true; function.blocks.len()];
    let mut work = 0usize;
    while let Some(block) = queue.pop_front() {
        queued[block] = false;
        work = work
            .checked_add(words * (predecessors[block].len() + 2))
            .filter(|&n| n <= 16_777_216)
            .ok_or("Local definite-assignment analysis exceeds resource limit")?;
        let mut state = if block == 0 {
            vec![0; words]
        } else {
            full.clone()
        };
        for &previous in &predecessors[block] {
            for (word, &other) in state.iter_mut().zip(&outgoing[previous]) {
                *word &= other;
            }
        }
        incoming[block].clone_from(&state);
        for (word, &written) in state.iter_mut().zip(&writes[block]) {
            *word |= written;
        }
        if state != outgoing[block] {
            outgoing[block] = state;
            for edge in function.blocks[block].terminator.kind.edges() {
                if !queued[edge.block] {
                    queue.push_back(edge.block);
                    queued[edge.block] = true;
                }
            }
        }
    }
    Ok(incoming)
}
