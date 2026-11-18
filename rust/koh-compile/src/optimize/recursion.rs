use crate::ir::*;
use std::collections::{BTreeMap, BTreeSet};

#[derive(Clone, Copy)]
enum Expression {
    Value(u32),
    Recursive,
    Sum(u32),
}

fn candidate(function: &Function) -> Option<Function> {
    let ty = function.returns?;
    let integer = |ty| matches!(ty, Type::I32 | Type::I64);
    if !integer(ty)
        || !function
            .parameters
            .iter()
            .chain(&function.locals)
            .copied()
            .all(integer)
    {
        return None;
    }
    let mut site = None;
    for (block, body) in function.blocks.iter().enumerate() {
        if matches!(
            body.terminator.kind,
            Control::TypeFault | Control::RuntimeFault(_)
        ) {
            return None;
        }
        for (index, instruction) in body.instructions.iter().enumerate() {
            if instruction.result.is_some_and(|r| !integer(r.1)) {
                return None;
            }
            match &instruction.op {
                Op::Call { target, .. } if *target == function.identity && site.is_none() => {
                    site = Some((block, index))
                }
                Op::Binary { op, .. }
                    if !matches!(
                        op,
                        Binary::DivideSigned
                            | Binary::DivideUnsigned
                            | Binary::RemainderSigned
                            | Binary::RemainderUnsigned
                    ) => {}
                Op::Constant(_)
                | Op::Compare { .. }
                | Op::Convert { .. }
                | Op::Negate(_)
                | Op::Not(_)
                | Op::StoreLocal { .. }
                | Op::LoadLocal(_) => {}
                _ => return None,
            }
        }
    }
    let (call_block, call_index) = site?;
    let definitions: BTreeMap<_, _> = function
        .blocks
        .iter()
        .flat_map(|b| &b.instructions)
        .filter_map(|i| i.result.map(|(id, _)| (id, i)))
        .collect();
    let mut next = function
        .blocks
        .iter()
        .flat_map(|b| {
            b.parameters
                .iter()
                .map(|p| p.0)
                .chain(b.instructions.iter().filter_map(|i| i.result.map(|r| r.0)))
        })
        .max()?
        .checked_add(1)?;
    let mut fresh = || {
        let id = next;
        next = next.checked_add(1)?;
        Some(id)
    };
    let mut initialized = BTreeSet::new();
    for instruction in &function.blocks[0].instructions {
        match instruction.op {
            Op::StoreLocal { local, .. } => {
                initialized.insert(local);
            }
            Op::LoadLocal(local) if !initialized.contains(&local) => return None,
            _ => {}
        }
    }
    if function
        .blocks
        .iter()
        .flat_map(|b| &b.instructions)
        .any(|i| matches!(i.op, Op::LoadLocal(local) if !initialized.contains(&local)))
    {
        return None;
    }
    let returns: Vec<_> = function
        .blocks
        .iter()
        .filter_map(|b| match b.terminator.kind {
            Control::Return(Some(id)) => Some(id),
            _ => None,
        })
        .collect();
    let normalization = |id| match definitions.get(&id)?.op {
        Op::Convert { bits, signed, .. } if u16::from(bits) < ty.bits() => Some((bits, signed)),
        _ => None,
    };
    let (bits, signed) = if let Some(normal) = returns.iter().find_map(|&id| normalization(id)) {
        if !returns.iter().all(|&id| normalization(id) == Some(normal)) {
            return None;
        }
        normal
    } else {
        (ty.bits() as u8, false)
    };
    let call = &function.blocks[call_block].instructions[call_index];
    let (call_result, call_type) = call.result?;
    if call_type != ty {
        return None;
    }
    let Op::Call { arguments, .. } = &call.op else {
        return None;
    };
    let mut expressions = BTreeMap::new();
    for &(id, value_ty) in &function.blocks[0].parameters {
        if value_ty == ty {
            expressions.insert(id, Expression::Value(id));
        }
    }
    for instruction in &function.blocks[call_block].instructions[..call_index] {
        if let Some((id, _)) = instruction.result.filter(|r| r.1 == ty) {
            expressions.insert(id, Expression::Value(id));
        }
    }
    expressions.insert(call_result, Expression::Recursive);
    let mut locals = BTreeMap::new();
    let mut extras = Vec::new();
    let mut block = call_block;
    let mut start = call_index + 1;
    let mut visited = BTreeSet::new();
    let contribution = loop {
        if !visited.insert(block) {
            return None;
        }
        let body = &function.blocks[block];
        for instruction in &body.instructions[start..] {
            let expression = match instruction.op {
                Op::Constant(value) if instruction.result?.1 == ty => {
                    let id = fresh()?;
                    extras.push(Instruction {
                        result: Some((id, ty)),
                        op: Op::Constant(value),
                        il_offset: instruction.il_offset,
                    });
                    Some(Expression::Value(id))
                }
                Op::Convert {
                    value, bits: width, ..
                } if width >= bits && instruction.result?.1 == ty => {
                    Some(*expressions.get(&value)?)
                }
                Op::Binary {
                    op: Binary::Add,
                    left,
                    right,
                } => match (expressions.get(&left)?, expressions.get(&right)?) {
                    (Expression::Recursive, Expression::Value(value))
                    | (Expression::Value(value), Expression::Recursive) => {
                        Some(Expression::Sum(*value))
                    }
                    _ => return None,
                },
                Op::StoreLocal { local, value } => {
                    locals.insert(local, *expressions.get(&value)?);
                    None
                }
                Op::LoadLocal(local) => Some(*locals.get(&local)?),
                _ => return None,
            };
            if let Some(expression) = expression {
                expressions.insert(instruction.result?.0, expression);
            }
        }
        match &body.terminator.kind {
            Control::Return(Some(value)) => {
                let Expression::Sum(value) = expressions.get(value)? else {
                    return None;
                };
                break *value;
            }
            Control::Jump(edge) => {
                let passed = edge
                    .arguments
                    .iter()
                    .map(|id| expressions.get(id).copied())
                    .collect::<Option<Vec<_>>>()?;
                for (&(id, _), expression) in
                    function.blocks[edge.block].parameters.iter().zip(passed)
                {
                    expressions.insert(id, expression);
                }
                block = edge.block;
                start = 0;
            }
            _ => return None,
        }
    };
    let accumulator = u32::try_from(function.locals.len()).ok()?;
    let mut result = function.clone();
    result.locals.push(ty);
    for body in &mut result.blocks {
        for edge in body.terminator.kind.edges_mut() {
            edge.block += 1;
        }
    }
    let body = &mut result.blocks[call_block];
    body.instructions.truncate(call_index);
    body.instructions.extend(extras);
    let mut add = |instructions: &mut Vec<Instruction>, value, il_offset| -> Option<u32> {
        let load = fresh()?;
        let sum = fresh()?;
        let normalized = fresh()?;
        instructions.extend([
            Instruction {
                result: Some((load, ty)),
                op: Op::LoadLocal(accumulator),
                il_offset,
            },
            Instruction {
                result: Some((sum, ty)),
                op: Op::Binary {
                    op: Binary::Add,
                    left: load,
                    right: value,
                },
                il_offset,
            },
            Instruction {
                result: Some((normalized, ty)),
                op: Op::Convert {
                    value: sum,
                    bits,
                    signed,
                },
                il_offset,
            },
        ]);
        Some(normalized)
    };
    let value = add(&mut body.instructions, contribution, call.il_offset)?;
    body.instructions.push(Instruction {
        result: None,
        op: Op::StoreLocal {
            local: accumulator,
            value,
        },
        il_offset: call.il_offset,
    });
    body.terminator = Terminator {
        kind: Control::Jump(Edge {
            block: 1,
            arguments: arguments.clone(),
        }),
        il_offset: call.il_offset,
    };
    for body in &mut result.blocks {
        if let Control::Return(Some(value)) = body.terminator.kind {
            let value = add(&mut body.instructions, value, body.terminator.il_offset)?;
            body.terminator.kind = Control::Return(Some(value));
        }
    }
    let parameters = function
        .parameters
        .iter()
        .map(|&ty| Some((fresh()?, ty)))
        .collect::<Option<Vec<_>>>()?;
    let zero = fresh()?;
    let mut instructions = vec![
        Instruction {
            result: Some((zero, ty)),
            op: Op::Constant(0),
            il_offset: 0,
        },
        Instruction {
            result: None,
            op: Op::StoreLocal {
                local: accumulator,
                value: zero,
            },
            il_offset: 0,
        },
    ];
    for local in initialized {
        let zero = fresh()?;
        let local_ty = function.locals[local as usize];
        instructions.extend([
            Instruction {
                result: Some((zero, local_ty)),
                op: Op::Constant(0),
                il_offset: 0,
            },
            Instruction {
                result: None,
                op: Op::StoreLocal { local, value: zero },
                il_offset: 0,
            },
        ]);
    }
    result.blocks.insert(
        0,
        Block {
            parameters: parameters.clone(),
            instructions,
            terminator: Terminator {
                kind: Control::Jump(Edge {
                    block: 1,
                    arguments: parameters.iter().map(|p| p.0).collect(),
                }),
                il_offset: 0,
            },
        },
    );
    let mut live = BTreeSet::new();
    let mut pending = vec![0];
    while let Some(block) = pending.pop() {
        if live.insert(block) {
            pending.extend(
                result.blocks[block]
                    .terminator
                    .kind
                    .edges()
                    .map(|e| e.block),
            );
        }
    }
    let remap: BTreeMap<_, _> = live
        .iter()
        .enumerate()
        .map(|(new, &old)| (old, new))
        .collect();
    result.blocks = result
        .blocks
        .into_iter()
        .enumerate()
        .filter_map(|(index, mut body)| {
            if !live.contains(&index) {
                return None;
            }
            for edge in body.terminator.kind.edges_mut() {
                edge.block = remap[&edge.block];
            }
            Some(body)
        })
        .collect();
    verify(&result).ok()?;
    Some(result)
}

pub(super) fn apply(function: &mut Function) {
    if let Some(result) = candidate(function) {
        *function = result;
    }
}
