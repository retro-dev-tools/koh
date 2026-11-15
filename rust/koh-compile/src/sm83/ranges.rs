use crate::ir::*;
use std::collections::BTreeMap;

pub(super) struct Widths {
    pub values: BTreeMap<u32, u16>,
    pub locals: Vec<u16>,
}

fn mask(bits: u8) -> u64 {
    if bits == 64 {
        u64::MAX
    } else {
        (1u64 << bits) - 1
    }
}

fn width(ty: Type, maximum: u64) -> u16 {
    if matches!(ty, Type::I32 | Type::I64) {
        [1, 2, 4, 8]
            .into_iter()
            .find(|&bytes| bytes == 8 || maximum < 1u64 << (bytes * 8))
            .unwrap_or(8)
    } else {
        u16::from(ty.bits() / 8)
    }
}

pub(super) fn analyze(function: &Function) -> Widths {
    let types: BTreeMap<_, _> = function
        .blocks
        .iter()
        .flat_map(|b| {
            b.parameters
                .iter()
                .copied()
                .chain(b.instructions.iter().filter_map(|i| i.result))
        })
        .collect();
    let mut maximum: BTreeMap<_, _> = types
        .iter()
        .map(|(&id, &ty)| (id, mask(ty.bits())))
        .collect();
    let mut locals: Vec<_> = function.locals.iter().map(|t| mask(t.bits())).collect();
    let mut aliases = vec![0; locals.len()];
    let mut minimum = vec![0; locals.len()];
    for i in function.blocks.iter().flat_map(|b| &b.instructions) {
        if let Op::LocalAddress { local, scalar } = i.op {
            let index = local as usize;
            let maximum = if matches!(
                scalar,
                Scalar::Bool | Scalar::U8 | Scalar::Char | Scalar::U16 | Scalar::U32 | Scalar::U64
            ) {
                mask(scalar.bits())
            } else {
                mask(function.locals[index].bits())
            };
            aliases[index] = aliases[index].max(maximum);
            minimum[index] = minimum[index].max(u16::from(scalar.bits() / 8));
        }
    }
    let mut incoming = vec![vec![]; function.blocks.len()];
    for b in &function.blocks {
        for edge in b.terminator.kind.edges() {
            incoming[edge.block].push(edge);
        }
    }
    let constants: BTreeMap<_, _> = function
        .blocks
        .iter()
        .flat_map(|b| &b.instructions)
        .filter_map(|i| match (i.result, &i.op) {
            (Some((id, _)), Op::Constant(bits)) => Some((id, *bits)),
            _ => None,
        })
        .collect();
    for _ in 0..64 {
        let mut changed = false;
        let mut stored = vec![0; aliases.len()];
        for (block, body) in function.blocks.iter().enumerate() {
            if block != 0 {
                for (slot, &(id, _)) in body.parameters.iter().enumerate() {
                    let bound = incoming[block]
                        .iter()
                        .map(|e| maximum[&e.arguments[slot]])
                        .max()
                        .unwrap_or(maximum[&id]);
                    if bound < maximum[&id] {
                        maximum.insert(id, bound);
                        changed = true;
                    }
                }
            }
            for i in &body.instructions {
                if let Op::StoreLocal { local, value } = i.op {
                    stored[local as usize] = stored[local as usize].max(maximum[&value]);
                }
                let Some((id, ty)) = i.result else {
                    continue;
                };
                let full = mask(ty.bits());
                let bound = match i.op {
                    Op::Constant(value) => value,
                    Op::Compare { .. } => 1,
                    Op::LoadLocal(local) => locals[local as usize],
                    Op::LoadMemory {
                        bits,
                        signed: false,
                        ..
                    } => mask(bits),
                    Op::Convert {
                        value,
                        bits,
                        signed,
                    } => {
                        let copied = bits.min(types[&value].bits()).min(ty.bits());
                        let bound = maximum[&value].min(mask(copied));
                        if signed && bound >= 1u64 << (copied - 1) {
                            full
                        } else {
                            bound
                        }
                    }
                    Op::Binary { op, left, right } => {
                        let a = maximum[&left];
                        let b = maximum[&right];
                        let bounded = |n: u128| {
                            if n <= u128::from(full) {
                                n as u64
                            } else {
                                full
                            }
                        };
                        match op {
                            Binary::Add => bounded(u128::from(a) + u128::from(b)),
                            Binary::Multiply => bounded(u128::from(a) * u128::from(b)),
                            Binary::And => a.min(b),
                            Binary::Or | Binary::Xor => {
                                let bits = 64 - a.max(b).leading_zeros() as u8;
                                mask(bits)
                            }
                            Binary::Subtract if b == 0 => a,
                            Binary::ShiftRightUnsigned => constants
                                .get(&right)
                                .map_or(a, |c| a >> (c & u64::from(ty.bits() - 1))),
                            Binary::ShiftRightSigned if a < 1u64 << (ty.bits() - 1) => constants
                                .get(&right)
                                .map_or(a, |c| a >> (c & u64::from(ty.bits() - 1))),
                            Binary::ShiftLeft if a == 0 => 0,
                            Binary::ShiftLeft => constants.get(&right).map_or(full, |c| {
                                bounded(u128::from(a) << (c & u64::from(ty.bits() - 1)))
                            }),
                            _ => full,
                        }
                    }
                    Op::Negate(value) if maximum[&value] == 0 => 0,
                    _ => full,
                };
                if bound < maximum[&id] {
                    maximum.insert(id, bound);
                    changed = true;
                }
            }
        }
        for (index, bound) in stored.into_iter().enumerate() {
            let bound = bound | aliases[index];
            if bound < locals[index] {
                locals[index] = bound;
                changed = true;
            }
        }
        if !changed {
            break;
        }
    }
    Widths {
        values: types
            .iter()
            .map(|(&id, &ty)| (id, width(ty, maximum[&id])))
            .collect(),
        locals: function
            .locals
            .iter()
            .enumerate()
            .map(|(i, &ty)| width(ty, locals[i]).max(minimum[i]))
            .collect(),
    }
}
