use crate::ir::*;
use std::collections::{BTreeMap, HashMap, HashSet, VecDeque};
mod recursion;

pub(crate) fn constant_calls(functions: &mut [Function]) -> Result<(), String> {
    let constants: BTreeMap<_, _> = functions
        .iter()
        .filter_map(|function| {
            if !function.parameters.is_empty()
                || function.blocks.len() != 1
                || !matches!(function.returns, Some(Type::I32 | Type::Native | Type::I64))
            {
                return None;
            }
            let block = &function.blocks[0];
            let Control::Return(Some(value)) = block.terminator.kind else {
                return None;
            };
            let mut values = BTreeMap::<u32, (u64, Type)>::new();
            for instruction in &block.instructions {
                let (id, ty) = instruction.result?;
                let bits = match instruction.op {
                    Op::Constant(bits) => bits,
                    Op::Convert {
                        value,
                        bits,
                        signed,
                    } => {
                        let (value, source) = *values.get(&value)?;
                        let retained =
                            u16::from(bits).min(ty.bits()).min(source.bits()) - u16::from(signed);
                        if retained < 64 && value >= 1u64 << retained {
                            return None;
                        }
                        value
                    }
                    _ => return None,
                };
                values.insert(id, (bits, ty));
            }
            values
                .get(&value)
                .map(|&(bits, _)| ((function.identity.assembly, function.identity.token), bits))
        })
        .collect();
    for function in functions {
        let mut changed = false;
        for instruction in function.blocks.iter_mut().flat_map(|b| &mut b.instructions) {
            if let Op::Call {
                target, arguments, ..
            } = &instruction.op
            {
                if arguments.is_empty() && instruction.result.is_some() {
                    if let Some(&bits) = constants.get(&(target.assembly, target.token)) {
                        instruction.op = Op::Constant(bits);
                        changed = true;
                    }
                }
            }
        }
        if changed {
            simplify(function)?;
        }
    }
    Ok(())
}

#[cfg(test)]
mod constant_call_tests {
    use super::*;
    use crate::resolve::Definition;
    use rstest::rstest;

    #[rstest]
    #[case(Type::I32, Type::I32, 128, 8, false, false, true)]
    #[case(Type::I32, Type::I32, 256, 8, false, false, false)]
    #[case(Type::I32, Type::I32, 128, 8, true, false, false)]
    #[case(Type::I32, Type::I32, 127, 8, true, false, true)]
    #[case(Type::I64, Type::I64, u64::MAX, 64, false, false, true)]
    #[case(Type::I32, Type::I32, 42, 8, false, true, false)]
    #[case(Type::I32, Type::I64, 0xffffffff, 64, true, false, false)]
    #[case(Type::Native, Type::I32, 0xffff, 32, true, false, false)]
    fn folds_only_effect_free_unchanged_integer_returns(
        #[case] source: Type,
        #[case] ty: Type,
        #[case] value: u64,
        #[case] bits: u8,
        #[case] signed: bool,
        #[case] effect: bool,
        #[case] folded: bool,
    ) {
        let target = Definition {
            assembly: 0,
            token: 0x06000002,
        };
        let mut callee = Function {
            identity: target,
            parameters: vec![],
            returns: Some(ty),
            locals: vec![],
            blocks: vec![Block {
                parameters: vec![],
                instructions: vec![
                    Instruction {
                        result: Some((0, source)),
                        op: Op::Constant(value),
                        il_offset: 0,
                    },
                    Instruction {
                        result: Some((1, ty)),
                        op: Op::Convert {
                            value: 0,
                            bits,
                            signed,
                        },
                        il_offset: 1,
                    },
                ],
                terminator: Terminator {
                    kind: Control::Return(Some(1)),
                    il_offset: 2,
                },
            }],
        };
        if effect {
            callee.blocks[0].instructions.push(Instruction {
                result: None,
                op: Op::CpuControl(CpuControl::DisableInterrupts),
                il_offset: 2,
            });
        }
        let caller = Function {
            identity: Definition {
                assembly: 0,
                token: 0x06000001,
            },
            parameters: vec![],
            returns: Some(ty),
            locals: vec![],
            blocks: vec![Block {
                parameters: vec![],
                instructions: vec![Instruction {
                    result: Some((0, ty)),
                    op: Op::Call {
                        target,
                        signature: Signature {
                            parameters: vec![],
                            returns: Some(ty),
                        },
                        arguments: vec![],
                    },
                    il_offset: 17,
                }],
                terminator: Terminator {
                    kind: Control::Return(Some(0)),
                    il_offset: 18,
                },
            }],
        };
        let mut functions = [callee, caller];
        verify_program(&functions).unwrap();
        constant_calls(&mut functions).unwrap();
        verify_program(&functions).unwrap();
        let instruction = &functions[1].blocks[0].instructions[0];
        assert_eq!(instruction.il_offset, 17);
        assert_eq!(
            matches!(instruction.op, Op::Constant(v) if v == value),
            folded
        );
        assert_eq!(matches!(instruction.op, Op::Call { .. }), !folded);
    }
}

fn forward_locals(function: &Function) -> BTreeMap<u32, u32> {
    let count = function.locals.len();
    let mut aliases = BTreeMap::new();
    if count == 0 || count.saturating_mul(function.blocks.len()) > 1_000_000 {
        return aliases;
    }
    let mut eligible = vec![true; count];
    let mut predecessors = vec![vec![]; function.blocks.len()];
    for (index, block) in function.blocks.iter().enumerate() {
        for i in &block.instructions {
            if let Op::LocalAddress { local, .. } | Op::StorePinned { local, .. } = i.op {
                eligible[local as usize] = false;
            }
        }
        for edge in block.terminator.kind.edges() {
            predecessors[edge.block].push(index);
        }
    }
    let incoming = |block: usize, states: &[Vec<Option<u32>>]| {
        let mut values = vec![None; count];
        if block != 0 {
            if let Some((&first, rest)) = predecessors[block].split_first() {
                values.clone_from(&states[first]);
                for &previous in rest {
                    for (value, other) in values.iter_mut().zip(&states[previous]) {
                        if value != other {
                            *value = None;
                        }
                    }
                }
            }
        }
        values
    };
    let mut outgoing = vec![vec![None; count]; function.blocks.len()];
    let mut pending: VecDeque<_> = (0..function.blocks.len()).collect();
    let mut queued = vec![true; function.blocks.len()];
    while let Some(index) = pending.pop_front() {
        queued[index] = false;
        let mut values = incoming(index, &outgoing);
        for i in &function.blocks[index].instructions {
            if let Op::StoreLocal { local, value } = i.op {
                if eligible[local as usize] {
                    values[local as usize] = Some(value);
                }
            }
        }
        if values != outgoing[index] {
            outgoing[index] = values;
            for edge in function.blocks[index].terminator.kind.edges() {
                if !queued[edge.block] {
                    pending.push_back(edge.block);
                    queued[edge.block] = true;
                }
            }
        }
    }
    for (index, block) in function.blocks.iter().enumerate() {
        let mut values = incoming(index, &outgoing);
        for i in &block.instructions {
            match i.op {
                Op::StoreLocal { local, value } if eligible[local as usize] => {
                    values[local as usize] = Some(value);
                }
                Op::LoadLocal(local) if eligible[local as usize] => {
                    if let (Some((id, _)), Some(value)) = (i.result, values[local as usize]) {
                        aliases.insert(id, value);
                    }
                }
                _ => {}
            }
        }
    }
    aliases
}

fn pure(op: &Op) -> bool {
    matches!(
        op,
        Op::Constant(_)
            | Op::Reinterpret(_)
            | Op::ZeroStruct
            | Op::FieldHandle(_)
            | Op::MethodHandle(_)
            | Op::StringLiteral(_)
            | Op::LoadLocal(_)
            | Op::Convert { .. }
            | Op::Compare { .. }
            | Op::Negate(_)
            | Op::Not(_)
    ) || matches!(op, Op::Binary { op, .. } if !matches!(op,
            Binary::DivideSigned | Binary::DivideUnsigned | Binary::RemainderSigned | Binary::RemainderUnsigned))
}

fn remove_unused(function: &mut Function) {
    let mut definitions = BTreeMap::new();
    let mut pending = Vec::new();
    for block in &function.blocks {
        for instruction in &block.instructions {
            if let Some((id, _)) = instruction.result.filter(|_| pure(&instruction.op)) {
                definitions.insert(id, instruction.op.operands().collect::<Vec<_>>());
            } else {
                pending.extend(instruction.op.operands());
            }
        }
        for edge in block.terminator.kind.edges() {
            pending.extend(&edge.arguments);
        }
        match block.terminator.kind {
            Control::Return(Some(id))
            | Control::Branch { condition: id, .. }
            | Control::Switch { selector: id, .. } => pending.push(id),
            _ => {}
        }
    }
    let mut used = HashSet::new();
    while let Some(id) = pending.pop() {
        if used.insert(id) {
            if let Some(operands) = definitions.get(&id) {
                pending.extend(operands);
            }
        }
    }
    for block in &mut function.blocks {
        block
            .instructions
            .retain(|i| !pure(&i.op) || i.result.is_none_or(|(id, _)| used.contains(&id)));
    }
}

pub fn simplify(function: &mut Function) -> Result<(), String> {
    verify(function)?;
    let mut aliases = forward_locals(function);
    let mut predecessors = vec![0usize; function.blocks.len()];
    for block in &function.blocks {
        for edge in block.terminator.kind.edges() {
            predecessors[edge.block] += 1;
        }
    }
    let mut blocks: Vec<_> = std::mem::take(&mut function.blocks)
        .into_iter()
        .map(Some)
        .collect();
    let mut owner: Vec<_> = (0..blocks.len()).collect();
    for index in 0..blocks.len() {
        let Some(mut block) = blocks[index].take() else {
            continue;
        };
        while let Control::Jump(edge) = &block.terminator.kind {
            let target = edge.block;
            if target == 0 || target == index || predecessors[target] != 1 {
                break;
            }
            let Some(next) = blocks[target].take() else {
                break;
            };
            for (&(parameter, _), &argument) in next.parameters.iter().zip(&edge.arguments) {
                aliases.insert(parameter, argument);
            }
            owner[target] = index;
            block.instructions.extend(next.instructions);
            block.terminator = next.terminator;
        }
        blocks[index] = Some(block);
    }
    for block in blocks.iter().flatten() {
        let mut locals = BTreeMap::new();
        let mut constants = HashMap::new();
        for instruction in &block.instructions {
            match instruction.op {
                Op::Constant(bits) => {
                    if let Some((id, ty)) = instruction.result {
                        if let Some(&value) = constants.get(&(ty, bits)) {
                            aliases.insert(id, value);
                        } else {
                            constants.insert((ty, bits), id);
                        }
                    }
                }
                Op::StoreLocal { local, value } => {
                    locals.insert(local, value);
                }
                Op::LoadLocal(local) => {
                    if let Some((id, _)) = instruction.result {
                        if let Some(&value) = locals.get(&local) {
                            aliases.insert(id, value);
                        } else {
                            locals.insert(local, id);
                        }
                    }
                }
                _ if pure(&instruction.op) => {}
                _ => locals.clear(),
            }
        }
    }
    let resolve = |mut value| -> Result<u32, String> {
        for _ in 0..=aliases.len() {
            let Some(&next) = aliases.get(&value) else {
                return Ok(value);
            };
            value = next;
        }
        Err("Cyclic value aliases during CFG simplification".into())
    };
    let mut resolved = BTreeMap::new();
    for &value in aliases.keys() {
        resolved.insert(value, resolve(value)?);
    }
    let value = |id: &mut u32| {
        *id = resolved.get(id).copied().unwrap_or(*id);
    };
    let mut remap = vec![0; blocks.len()];
    let mut count = 0;
    for (index, block) in blocks.iter().enumerate() {
        if block.is_some() {
            remap[index] = count;
            count += 1;
        }
    }
    for index in 0..owner.len() {
        let mut root = index;
        while owner[root] != root {
            root = owner[root];
        }
        remap[index] = remap[root];
    }
    for mut block in blocks.into_iter().flatten() {
        for instruction in &mut block.instructions {
            match &mut instruction.op {
                Op::MemoryFill {
                    address,
                    value: stored,
                    count,
                }
                | Op::MemoryCopy {
                    destination: address,
                    source: stored,
                    count,
                }
                | Op::VBlankCopy {
                    destination: address,
                    source: stored,
                    count,
                }
                | Op::HBlankStream {
                    source: address,
                    first: stored,
                    count,
                    ..
                } => {
                    value(address);
                    value(stored);
                    value(count);
                }
                Op::ArenaAllocate(bytes) | Op::StackAllocate { bytes } | Op::OamDma(bytes) => {
                    value(bytes)
                }
                Op::NewRectangle { rows, columns, .. } => {
                    value(rows);
                    value(columns);
                }
                Op::RectangleAddress { array, row, column } => {
                    value(array);
                    value(row);
                    value(column);
                }
                Op::ArrayDimension { array, dimension } => {
                    value(array);
                    value(dimension);
                }
                Op::InitializeArray { array, .. } => value(array),
                Op::ByteSpanElement { span, index } => {
                    value(span);
                    value(index);
                }
                Op::NewArray { length, .. }
                | Op::ByteSpanLiteral { length, .. }
                | Op::ArrayLength(length)
                | Op::StringLength(length) => value(length),
                Op::StringChar { string, index } => {
                    value(string);
                    value(index);
                }
                Op::ArrayAddress { array, index } => {
                    value(array);
                    value(index);
                }
                Op::LoadMemory { address, .. } | Op::FieldAddress { address, .. } => value(address),
                Op::ExtractField {
                    value: extracted, ..
                } => value(extracted),
                Op::StoreMemory {
                    address,
                    value: stored,
                    ..
                }
                | Op::VramWrite {
                    address,
                    value: stored,
                }
                | Op::PaletteWrite {
                    index: address,
                    color: stored,
                    ..
                } => {
                    value(address);
                    value(stored);
                }
                Op::Binary { left, right, .. } | Op::Compare { left, right, .. } => {
                    value(left);
                    value(right);
                }
                Op::Reinterpret(id)
                | Op::Negate(id)
                | Op::Upcast { value: id, .. }
                | Op::CheckObject(id)
                | Op::IsClass { object: id, .. }
                | Op::CastClass { object: id, .. }
                | Op::TryCastClass { object: id, .. }
                | Op::ObjectFieldAddress { object: id, .. }
                | Op::RequireRange(id)
                | Op::Not(id)
                | Op::Convert { value: id, .. }
                | Op::StoreLocal { value: id, .. }
                | Op::StorePinned { value: id, .. } => value(id),
                Op::NewDelegate { receiver, .. } => value(receiver),
                Op::Call { arguments, .. } | Op::InvokeDelegate { arguments, .. } => {
                    for id in arguments {
                        value(id);
                    }
                }
                Op::ArenaReset
                | Op::NewObject(_)
                | Op::CpuControl(_)
                | Op::Constant(_)
                | Op::ZeroStruct
                | Op::FieldHandle(_)
                | Op::MethodHandle(_)
                | Op::LocalAddress { .. }
                | Op::StaticAddress { .. }
                | Op::InitializerAddress(_)
                | Op::LoadLocal(_)
                | Op::StringLiteral(_) => {}
            }
        }
        let edge = |edge: &mut Edge| {
            edge.block = remap[edge.block];
            for id in &mut edge.arguments {
                value(id);
            }
        };
        match &mut block.terminator.kind {
            Control::Branch { condition, .. } => {
                value(condition);
            }
            Control::Switch { selector, .. } => value(selector),
            Control::Jump(_) | Control::TypeFault | Control::RuntimeFault(_) => {}
            Control::Return(result) => {
                if let Some(id) = result {
                    value(id);
                }
            }
        }
        for jump in block.terminator.kind.edges_mut() {
            edge(jump);
        }
        function.blocks.push(block);
    }
    remove_unused(function);
    let read: HashSet<_> = function
        .blocks
        .iter()
        .flat_map(|b| &b.instructions)
        .filter_map(|i| match i.op {
            Op::LoadLocal(local)
            | Op::LocalAddress { local, .. }
            | Op::StorePinned { local, .. } => Some(local),
            _ => None,
        })
        .collect();
    for block in &mut function.blocks {
        block
            .instructions
            .retain(|i| !matches!(i.op, Op::StoreLocal { local, .. } if !read.contains(&local)));
    }
    remove_unused(function);
    recursion::apply(function);
    verify(function)
}
