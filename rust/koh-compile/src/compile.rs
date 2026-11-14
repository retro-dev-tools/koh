use crate::{
    ir::{Function, Op, verify_program},
    resolve::{Definition, Resolver},
};
use std::collections::{HashSet, VecDeque};

pub fn reachable(resolver: &Resolver<'_>, entry: Definition) -> Result<Vec<Function>, String> {
    let mut seen = HashSet::from([entry]);
    let mut queue = VecDeque::from([entry]);
    let mut functions = Vec::new();
    let mut instructions = 0usize;
    while let Some(identity) = queue.pop_front() {
        let function = crate::lower::assembly_method(resolver, identity)?;
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
                if let Op::Call { target, .. } = instruction.op {
                    if seen.insert(target) {
                        if seen.len() > 4096 {
                            return Err("Reachable method count exceeds 4096".into());
                        }
                        queue.push_back(target);
                    }
                }
            }
        }
        functions.push(function);
    }
    functions.sort_by_key(|f| (f.identity.assembly, f.identity.token));
    verify_program(&functions)?;
    Ok(functions)
}
