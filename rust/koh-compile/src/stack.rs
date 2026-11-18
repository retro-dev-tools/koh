use crate::{
    ir::{Function, Op},
    resolve::Definition,
};
use std::collections::{HashMap, HashSet, VecDeque};

pub(crate) enum Bound {
    Finite(u16),
    Recursive(Definition),
}

pub(crate) fn bound(
    functions: &[Function],
    indices: &HashMap<Definition, usize>,
    members: &HashSet<Definition>,
    roots: &[Definition],
    overhead: u16,
    context: &str,
) -> Result<Bound, (Definition, String)> {
    let fail = |id, message: &str| (id, format!("{context} {message}"));
    let mut pending = vec![0usize; functions.len()];
    let mut parents = vec![Vec::new(); functions.len()];
    let mut frames = vec![0u16; functions.len()];
    let mut needs = vec![0u16; functions.len()];
    let mut ready = VecDeque::new();
    for (index, function) in functions
        .iter()
        .enumerate()
        .filter(|(_, f)| members.contains(&f.identity))
    {
        frames[index] =
            crate::sm83::frame_size(function).map_err(|error| (function.identity, error))?;
        needs[index] = frames[index] + 4;
        for instruction in function.blocks.iter().flat_map(|b| &b.instructions) {
            if let Op::Call {
                target, signature, ..
            } = &instruction.op
            {
                let child = *indices
                    .get(target)
                    .ok_or_else(|| fail(function.identity, "call target is absent"))?;
                let bytes = signature
                    .parameters
                    .iter()
                    .chain(signature.returns.iter())
                    .map(|ty| usize::from(ty.bits() / 8))
                    .sum::<usize>()
                    + 6;
                let bytes = u16::try_from(bytes)
                    .map_err(|_| fail(function.identity, "call storage exceeds stack capacity"))?;
                parents[child].push((index, bytes));
                pending[index] += 1;
            }
        }
        if pending[index] == 0 {
            ready.push_back(index);
        }
    }
    let mut completed = 0;
    while let Some(child) = ready.pop_front() {
        completed += 1;
        for &(parent, bytes) in &parents[child] {
            let required = u32::from(frames[parent]) + u32::from(bytes) + u32::from(needs[child]);
            if required + u32::from(overhead) > 0xf00 {
                return Err(fail(
                    functions[parent].identity,
                    "call graph exceeds WRAM0 stack capacity",
                ));
            }
            needs[parent] = needs[parent].max(required as u16);
            pending[parent] -= 1;
            if pending[parent] == 0 {
                ready.push_back(parent);
            }
        }
    }
    if completed != members.len() {
        let member = functions
            .iter()
            .enumerate()
            .rfind(|(i, f)| members.contains(&f.identity) && pending[*i] != 0)
            .map(|(_, f)| f.identity)
            .ok_or_else(|| {
                fail(
                    Definition {
                        assembly: 0,
                        token: 0,
                    },
                    "call graph accounting is inconsistent",
                )
            })?;
        return Ok(Bound::Recursive(member));
    }
    let mut largest = 0;
    for &root in roots {
        let index = *indices
            .get(&root)
            .ok_or_else(|| fail(root, "call target is absent"))?;
        let required = u32::from(needs[index]) + u32::from(overhead);
        if required > 0xf00 {
            return Err(fail(root, "call graph exceeds WRAM0 stack capacity"));
        }
        largest = largest.max(required as u16);
    }
    Ok(Bound::Finite(largest))
}
