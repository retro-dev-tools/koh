use super::{Function, Instruction, Op, Type};
use std::collections::{BTreeMap, HashMap, HashSet};

#[derive(Clone, Copy)]
enum Value<'a> {
    Instruction(&'a Instruction),
    Parameter { block: usize, slot: usize, ty: Type },
}

struct Constants<'a> {
    values: HashMap<u32, Value<'a>>,
    incoming: Vec<Vec<&'a super::Edge>>,
    cache: HashMap<u32, Option<u64>>,
    active: HashSet<u32>,
}

impl Constants<'_> {
    fn ty(&self, id: u32) -> Type {
        match self.values[&id] {
            Value::Instruction(i) => i.result.expect("verified definition").1,
            Value::Parameter { ty, .. } => ty,
        }
    }

    fn get(&mut self, id: u32, depth: usize) -> Result<Option<u64>, String> {
        if let Some(&value) = self.cache.get(&id) {
            return Ok(value);
        }
        if depth > 128 {
            return Err("Stack allocation constant expression exceeds depth limit".into());
        }
        if !self.active.insert(id) {
            return Ok(None);
        }
        let value = match self.values[&id] {
            Value::Instruction(i) => match i.op {
                Op::Constant(bits) => Some(bits),
                Op::Convert {
                    value,
                    bits,
                    signed,
                } => match self.get(value, depth + 1)? {
                    None => None,
                    Some(source) => {
                        let target = self.ty(id).bits();
                        if target == 16 && source > 0xffff {
                            return Err(
                                "Stack allocation size conversion exceeds native address range"
                                    .into(),
                            );
                        }
                        let copied = bits.min(self.ty(value).bits()).min(target);
                        let mask = |bits| {
                            if bits == 64 {
                                u64::MAX
                            } else {
                                (1u64 << bits) - 1
                            }
                        };
                        let mut result = source & mask(copied);
                        if signed && result & (1u64 << (copied - 1)) != 0 {
                            result |= !mask(copied);
                        }
                        Some(result & mask(target))
                    }
                },
                _ => None,
            },
            Value::Parameter { block, slot, .. } => {
                let arguments: Vec<_> = self.incoming[block]
                    .iter()
                    .map(|edge| edge.arguments[slot])
                    .collect();
                let mut result = None;
                for (index, argument) in arguments.into_iter().enumerate() {
                    let next = self.get(argument, depth + 1)?;
                    if next.is_none() || (index != 0 && next != result) {
                        result = None;
                        break;
                    }
                    result = next;
                }
                result
            }
        };
        self.active.remove(&id);
        self.cache.insert(id, value);
        Ok(value)
    }
}

pub(crate) fn allocations(function: &Function) -> Result<BTreeMap<u32, u16>, String> {
    let sites: Vec<_> = function
        .blocks
        .iter()
        .enumerate()
        .flat_map(|(block, body)| {
            body.instructions
                .iter()
                .filter_map(move |i| matches!(i.op, Op::StackAllocate { .. }).then_some((block, i)))
        })
        .collect();
    if sites.is_empty() {
        return Ok(BTreeMap::new());
    }
    if sites.len() > 256 {
        return Err("Stack allocation site count exceeds 256".into());
    }
    let mut constants = Constants {
        values: HashMap::new(),
        incoming: vec![vec![]; function.blocks.len()],
        cache: HashMap::new(),
        active: HashSet::new(),
    };
    for (block, body) in function.blocks.iter().enumerate() {
        for (slot, &(id, ty)) in body.parameters.iter().enumerate() {
            constants
                .values
                .insert(id, Value::Parameter { block, slot, ty });
        }
        for i in &body.instructions {
            if let Some((id, _)) = i.result {
                constants.values.insert(id, Value::Instruction(i));
            }
        }
        for edge in body.terminator.kind.edges() {
            constants.incoming[edge.block].push(edge);
        }
    }
    let mut result = BTreeMap::new();
    let mut total = 0;
    for (block, i) in sites {
        let mut checked = || -> Result<(u32, u16), String> {
            let mut seen = vec![false; function.blocks.len()];
            let mut pending: Vec<_> = function.blocks[block]
                .terminator
                .kind
                .edges()
                .map(|e| e.block)
                .collect();
            while let Some(next) = pending.pop() {
                if next == block {
                    return Err(
                        "Stack allocation in a control-flow cycle requires dynamic stack lowering"
                            .into(),
                    );
                }
                if !seen[next] {
                    seen[next] = true;
                    pending.extend(
                        function.blocks[next]
                            .terminator
                            .kind
                            .edges()
                            .map(|e| e.block),
                    );
                }
            }
            let Op::StackAllocate { bytes } = i.op else {
                return Err("Invalid stack allocation site".into());
            };
            let bytes = constants
                .get(bytes, 0)?
                .ok_or("Stack allocation requires a statically known size")?;
            if bytes > 0xf00 {
                return Err("Stack allocation exceeds WRAM0 frame capacity".into());
            }
            Ok((
                i.result.ok_or("Stack allocation has no result")?.0,
                bytes as u16,
            ))
        };
        let (id, bytes) = checked().map_err(|error| format!("IL_{:04x}: {error}", i.il_offset))?;
        total += usize::from(bytes);
        if total > 0xf00 {
            return Err(format!(
                "IL_{:04x}: Stack allocations exceed WRAM0 frame capacity",
                i.il_offset
            ));
        }
        result.insert(id, bytes);
    }
    Ok(result)
}
