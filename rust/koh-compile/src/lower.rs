use crate::{
    cil::{Instruction as Cil, Operand, decode},
    ir::*,
    resolve::Definition,
};
use std::collections::{HashMap, VecDeque};

fn integer_type(ty: &clrmeta::TypeSig) -> Result<Type, String> {
    use clrmeta::{ElementType, TypeSig};
    match ty {
        TypeSig::Primitive(
            ElementType::I1
            | ElementType::U1
            | ElementType::I2
            | ElementType::U2
            | ElementType::Char
            | ElementType::I4
            | ElementType::U4
            | ElementType::Boolean,
        ) => Ok(Type::I32),
        TypeSig::Primitive(ElementType::I8 | ElementType::U8) => Ok(Type::I64),
        TypeSig::Primitive(ElementType::IntPtr | ElementType::UIntPtr) | TypeSig::Ptr(_) => {
            Ok(Type::Native)
        }
        _ => Err(format!("Type {ty:?} has no integer lowering yet")),
    }
}

fn integer_signature(signature: &clrmeta::MethodSig) -> Result<Signature, String> {
    if signature.calling_convention.has_this() || signature.generic_param_count != 0 {
        return Err("Instance/generic methods require instantiated lowering".into());
    }
    if signature.calling_convention.0 & 0x0f != 0 || signature.params.len() > 1024 {
        return Err("Unsupported calling convention or parameter count".into());
    }
    let parameters = signature
        .params
        .iter()
        .map(integer_type)
        .collect::<Result<_, _>>()?;
    let returns =
        if signature.return_type == clrmeta::TypeSig::Primitive(clrmeta::ElementType::Void) {
            None
        } else {
            Some(integer_type(&signature.return_type)?)
        };
    Ok(Signature {
        parameters,
        returns,
    })
}

pub fn assembly_method(
    resolver: &crate::resolve::Resolver<'_>,
    identity: Definition,
) -> Result<Function, String> {
    let assembly = resolver.assembly(identity.assembly)?;
    let result = || -> Result<Function, String> {
        crate::contracts::method(resolver, identity)?;
        if resolver.module_initializer(identity.assembly)?.is_some() {
            return Err("Module initializer requires static-initialization lowering".into());
        }
        if resolver.type_initializer(identity)?.is_some() {
            return Err("Type initializer requires static-initialization lowering".into());
        }
        let signature = assembly.method_signature(identity.token)?;
        let typed = integer_signature(&signature)?;
        let local_types = assembly.local_types(identity.token)?;
        let locals = local_types
            .iter()
            .map(integer_type)
            .collect::<Result<_, _>>()?;
        let body = assembly.method_body(identity.token)?;
        if body.extra_sections {
            return Err("Exception/extra method sections require explicit lowering".into());
        }
        let mut function = lower_with(
            Method {
                identity,
                il: body.il,
                parameters: typed.parameters,
                returns: typed.returns,
                locals,
                init_locals: body.init_locals,
                max_stack: body.max_stack,
            },
            Some(resolver),
        )?;
        let slots: Vec<_> = signature
            .params
            .iter()
            .chain(&local_types)
            .map(narrow_type)
            .collect();
        normalize_storage(&mut function, &slots, narrow_type(&signature.return_type))?;
        verify(&function)?;
        Ok(function)
    };
    result().map_err(|error| {
        format!(
            "{} IL_0000: {error}",
            assembly
                .method_name(identity.token)
                .unwrap_or_else(|_| format!("0x{:08x}", identity.token))
        )
    })
}

fn narrow_type(ty: &clrmeta::TypeSig) -> Option<(u8, bool)> {
    use clrmeta::{ElementType, TypeSig};
    match ty {
        TypeSig::Primitive(ElementType::I1) => Some((8, true)),
        TypeSig::Primitive(ElementType::U1 | ElementType::Boolean) => Some((8, false)),
        TypeSig::Primitive(ElementType::I2) => Some((16, true)),
        TypeSig::Primitive(ElementType::U2 | ElementType::Char) => Some((16, false)),
        _ => None,
    }
}

fn normalize_storage(
    function: &mut Function,
    slots: &[Option<(u8, bool)>],
    returns: Option<(u8, bool)>,
) -> Result<(), String> {
    let mut next = function
        .blocks
        .iter()
        .flat_map(|b| {
            b.parameters
                .iter()
                .copied()
                .chain(b.instructions.iter().filter_map(|i| i.result))
        })
        .map(|p| p.0)
        .max()
        .unwrap_or(0)
        + 1;
    let mut conversion = |value, (bits, signed), il_offset| -> Result<Instruction, String> {
        if next >= 262_144 {
            return Err("Storage conversions exceed value limit".into());
        }
        let id = next;
        next += 1;
        Ok(Instruction {
            result: Some((id, Type::I32)),
            op: Op::Convert {
                value,
                bits,
                signed,
            },
            il_offset,
        })
    };
    for block in &mut function.blocks {
        let mut instructions = Vec::new();
        for mut instruction in std::mem::take(&mut block.instructions) {
            if let Op::StoreLocal {
                local,
                ref mut value,
            } = instruction.op
            {
                if let Some(shape) = slots[local as usize] {
                    let convert = conversion(*value, shape, instruction.il_offset)?;
                    *value = next_result(&convert)?;
                    instructions.push(convert);
                }
            }
            instructions.push(instruction);
        }
        if let (Control::Return(Some(value)), Some(shape)) = (&mut block.terminator.kind, returns) {
            let convert = conversion(*value, shape, block.terminator.il_offset)?;
            *value = next_result(&convert)?;
            instructions.push(convert);
        }
        block.instructions = instructions;
    }
    Ok(())
}

fn next_result(instruction: &Instruction) -> Result<u32, String> {
    instruction
        .result
        .map(|r| r.0)
        .ok_or("Conversion has no result".into())
}

pub struct Method<'a> {
    pub identity: Definition,
    pub il: &'a [u8],
    pub parameters: Vec<Type>,
    pub returns: Option<Type>,
    pub locals: Vec<Type>,
    pub init_locals: bool,
    pub max_stack: u16,
}

struct Builder {
    next: u32,
    instructions: Vec<Instruction>,
    stack: Vec<(u32, Type)>,
    offset: u32,
}

impl Builder {
    fn value(&mut self, ty: Type, op: Op) -> Result<(u32, Type), String> {
        if self.next >= 262_144 {
            return Err("Lowered value count exceeds limit".into());
        }
        let id = self.next;
        self.next += 1;
        self.instructions.push(Instruction {
            result: Some((id, ty)),
            op,
            il_offset: self.offset,
        });
        Ok((id, ty))
    }

    fn pop(&mut self) -> Result<(u32, Type), String> {
        self.stack
            .pop()
            .ok_or("CIL evaluation stack underflow".into())
    }

    fn binary(&mut self, op: Binary) -> Result<(), String> {
        let mut right = self.pop()?;
        let mut left = self.pop()?;
        if matches!(op, Binary::Add | Binary::Subtract)
            && matches!(
                (left.1, right.1),
                (Type::Native, Type::I32) | (Type::I32, Type::Native)
            )
        {
            if left.1 == Type::I32 {
                left = self.value(
                    Type::Native,
                    Op::Convert {
                        value: left.0,
                        bits: 16,
                        signed: true,
                    },
                )?;
            }
            if right.1 == Type::I32 {
                right = self.value(
                    Type::Native,
                    Op::Convert {
                        value: right.0,
                        bits: 16,
                        signed: true,
                    },
                )?;
            }
        }
        let shift = matches!(
            op,
            Binary::ShiftLeft | Binary::ShiftRightSigned | Binary::ShiftRightUnsigned
        );
        if right.1 != if shift { Type::I32 } else { left.1 } {
            return Err("CIL binary operand type mismatch".into());
        }
        let value = self.value(
            left.1,
            Op::Binary {
                op,
                left: left.0,
                right: right.0,
            },
        )?;
        self.stack.push(value);
        Ok(())
    }

    fn compare(&mut self, op: Comparison) -> Result<(u32, Type), String> {
        let right = self.pop()?;
        let left = self.pop()?;
        if left.1 != right.1 {
            return Err("CIL comparison operand type mismatch".into());
        }
        self.value(
            Type::I32,
            Op::Compare {
                op,
                left: left.0,
                right: right.0,
            },
        )
    }

    fn edge(&self, block: usize) -> Edge {
        Edge {
            block,
            arguments: self.stack.iter().map(|v| v.0).collect(),
        }
    }
}

pub fn lower(method: Method<'_>) -> Result<Function, String> {
    lower_with(method, None)
}

fn lower_with(
    method: Method<'_>,
    resolver: Option<&crate::resolve::Resolver<'_>>,
) -> Result<Function, String> {
    lower_inner(&method, resolver).map_err(|error| {
        format!(
            "Assembly {} method 0x{:08x}: {error}",
            method.identity.assembly, method.identity.token
        )
    })
}

fn lower_inner(
    method: &Method<'_>,
    resolver: Option<&crate::resolve::Resolver<'_>>,
) -> Result<Function, String> {
    let cil = decode(method.il)?;
    if cil.len() >= 16_384
        || method.max_stack > 1024
        || method.locals.len() + method.parameters.len() > 1024
    {
        return Err("IL_0000: Method exceeds lowering resource limits".into());
    }
    if !method.init_locals && !method.locals.is_empty() {
        return Err("IL_0000: Uninitialized locals require definite-assignment lowering".into());
    }
    let indices: HashMap<_, _> = cil
        .iter()
        .enumerate()
        .map(|(i, op)| (op.offset, i + 1))
        .collect();
    let mut locals = method.parameters.clone();
    locals.extend_from_slice(&method.locals);
    let mut builder = Builder {
        next: method.parameters.len() as u32,
        instructions: vec![],
        stack: vec![],
        offset: 0,
    };
    for (i, _) in method.parameters.iter().enumerate() {
        builder.instructions.push(Instruction {
            result: None,
            op: Op::StoreLocal {
                local: i as u32,
                value: i as u32,
            },
            il_offset: 0,
        });
    }
    for (i, &ty) in method.locals.iter().enumerate() {
        let value = builder.value(ty, Op::Constant(0))?.0;
        builder.instructions.push(Instruction {
            result: None,
            op: Op::StoreLocal {
                local: (method.parameters.len() + i) as u32,
                value,
            },
            il_offset: 0,
        });
    }
    let entry = Block {
        parameters: method
            .parameters
            .iter()
            .copied()
            .enumerate()
            .map(|(i, ty)| (i as u32, ty))
            .collect(),
        instructions: std::mem::take(&mut builder.instructions),
        terminator: Terminator {
            il_offset: 0,
            kind: Control::Jump(Edge {
                block: 1,
                arguments: vec![],
            }),
        },
    };
    let mut blocks = vec![None; cil.len() + 1];
    blocks[0] = Some(entry);
    let mut incoming: Vec<Option<Vec<Type>>> = vec![None; cil.len() + 1];
    incoming[1] = Some(vec![]);
    let mut queue = VecDeque::from([1]);
    let mut total_parameters = 0usize;
    while let Some(block) = queue.pop_front() {
        let instruction = &cil[block - 1];
        builder.offset = instruction.offset;
        builder.stack.clear();
        let mut parameters = Vec::new();
        for &ty in incoming[block]
            .as_ref()
            .ok_or("IL_0000: Missing incoming stack")?
        {
            let id = builder.next;
            builder.next += 1;
            parameters.push((id, ty));
        }
        total_parameters += parameters.len();
        if total_parameters > 262_144 || builder.next > 262_144 {
            return Err(format!(
                "IL_{:04x}: Evaluation stack parameters exceed limit",
                instruction.offset
            ));
        }
        builder.stack.extend_from_slice(&parameters);
        let kind = translate(
            &mut builder,
            instruction,
            block,
            cil.len(),
            &indices,
            method,
            &locals,
            resolver,
        )
        .map_err(|error| format!("IL_{:04x}: {error}", instruction.offset))?;
        if builder.stack.len() > usize::from(method.max_stack) {
            return Err(format!(
                "IL_{:04x}: Evaluation stack exceeds declared maxstack",
                instruction.offset
            ));
        }
        let edges: Vec<&Edge> = match &kind {
            Control::Jump(edge) => vec![edge],
            Control::Branch { yes, no, .. } => vec![yes, no],
            Control::Return(_) => vec![],
        };
        let shape: Vec<_> = builder.stack.iter().map(|v| v.1).collect();
        for edge in edges {
            if let Some(expected) = &incoming[edge.block] {
                if expected != &shape {
                    return Err(format!(
                        "IL_{:04x}: Incompatible evaluation stacks at merge IL_{:04x}",
                        instruction.offset,
                        cil[edge.block - 1].offset
                    ));
                }
            } else {
                incoming[edge.block] = Some(shape.clone());
                queue.push_back(edge.block);
            }
        }
        blocks[block] = Some(Block {
            parameters,
            instructions: std::mem::take(&mut builder.instructions),
            terminator: Terminator {
                il_offset: instruction.offset,
                kind,
            },
        });
    }
    let mut remap = vec![0; blocks.len()];
    let mut next = 0;
    for (i, block) in blocks.iter().enumerate() {
        if block.is_some() {
            remap[i] = next;
            next += 1;
        }
    }
    let blocks = blocks
        .into_iter()
        .flatten()
        .map(|mut body| {
            match &mut body.terminator.kind {
                Control::Jump(edge) => edge.block = remap[edge.block],
                Control::Branch { yes, no, .. } => {
                    yes.block = remap[yes.block];
                    no.block = remap[no.block];
                }
                Control::Return(_) => {}
            }
            body
        })
        .collect();
    let function = Function {
        identity: method.identity,
        parameters: method.parameters.clone(),
        returns: method.returns,
        locals,
        blocks,
    };
    verify(&function)?;
    Ok(function)
}

fn translate(
    b: &mut Builder,
    i: &Cil,
    block: usize,
    count: usize,
    indices: &HashMap<u32, usize>,
    method: &Method<'_>,
    locals: &[Type],
    resolver: Option<&crate::resolve::Resolver<'_>>,
) -> Result<Control, String> {
    let next = || {
        if block < count {
            Ok(block + 1)
        } else {
            Err("CIL fallthrough past method end".to_string())
        }
    };
    let target = || -> Result<usize, String> {
        match i.operand {
            Operand::Target(offset) => indices
                .get(&offset)
                .copied()
                .ok_or("Invalid branch target".into()),
            _ => Err("Missing branch target".into()),
        }
    };
    let variable = || match i.operand {
        Operand::Variable(index) => Ok(usize::from(index)),
        _ => Err("Missing variable index".to_string()),
    };
    let load = |b: &mut Builder, index: usize| -> Result<(), String> {
        let ty = *locals.get(index).ok_or("Invalid local/argument index")?;
        let value = b.value(ty, Op::LoadLocal(index as u32))?;
        b.stack.push(value);
        Ok(())
    };
    let store = |b: &mut Builder, index: usize| -> Result<(), String> {
        let ty = *locals.get(index).ok_or("Invalid local/argument index")?;
        let value = b.pop()?;
        if value.1 != ty {
            return Err("Local/argument store type mismatch".into());
        }
        b.instructions.push(Instruction {
            result: None,
            op: Op::StoreLocal {
                local: index as u32,
                value: value.0,
            },
            il_offset: b.offset,
        });
        Ok(())
    };
    match i.opcode {
        0x28 => {
            let resolver =
                resolver.ok_or("CIL opcode 0x0028 requires assembly reference resolution")?;
            let token = match i.operand {
                Operand::Token(token) => token,
                _ => return Err("Missing call method token".into()),
            };
            let target = resolver.resolve_method(method.identity.assembly, token)?;
            if !target.type_arguments.is_empty() || !target.method_arguments.is_empty() {
                return Err("Generic call instantiation is not lowered yet".into());
            }
            let metadata = resolver.assembly(target.definition.assembly)?;
            let signature =
                integer_signature(&metadata.method_signature(target.definition.token)?)?;
            let mut arguments = Vec::with_capacity(signature.parameters.len());
            for &ty in signature.parameters.iter().rev() {
                let value = b.pop()?;
                if value.1 != ty {
                    return Err("CIL call argument type mismatch".into());
                }
                arguments.push(value.0);
            }
            arguments.reverse();
            if let Some(intrinsic) = crate::contracts::intrinsic(resolver, target.definition)? {
                use crate::contracts::Intrinsic;
                let address = match intrinsic {
                    Intrinsic::Read(a) | Intrinsic::Write(a) | Intrinsic::Region(a) => a,
                };
                let pointer = b.value(Type::Native, Op::Constant(u64::from(address)))?;
                match intrinsic {
                    Intrinsic::Read(_) => {
                        let value = b.value(
                            Type::I32,
                            Op::LoadMemory {
                                address: pointer.0,
                                bits: 8,
                                signed: false,
                            },
                        )?;
                        b.stack.push(value);
                    }
                    Intrinsic::Write(_) => b.instructions.push(Instruction {
                        result: None,
                        op: Op::StoreMemory {
                            address: pointer.0,
                            value: arguments[0],
                            bits: 8,
                        },
                        il_offset: b.offset,
                    }),
                    Intrinsic::Region(_) => b.stack.push(pointer),
                }
                return Ok(Control::Jump(b.edge(next()?)));
            }
            let returns = signature.returns;
            let op = Op::Call {
                target: target.definition,
                signature,
                arguments,
            };
            if let Some(ty) = returns {
                let value = b.value(ty, op)?;
                b.stack.push(value);
            } else {
                b.instructions.push(Instruction {
                    result: None,
                    op,
                    il_offset: b.offset,
                });
            }
        }
        0x00 => {}
        0x02..=0x05 => {
            let index = usize::from(i.opcode - 0x02);
            if index >= method.parameters.len() {
                return Err("Invalid argument index".into());
            }
            load(b, index)?;
        }
        0x06..=0x09 => load(b, method.parameters.len() + usize::from(i.opcode - 0x06))?,
        0x0a..=0x0d => store(b, method.parameters.len() + usize::from(i.opcode - 0x0a))?,
        0x0e | 0xfe09 => {
            let index = variable()?;
            if index >= method.parameters.len() {
                return Err("Invalid argument index".into());
            }
            load(b, index)?;
        }
        0x10 | 0xfe0b => {
            let index = variable()?;
            if index >= method.parameters.len() {
                return Err("Invalid argument index".into());
            }
            store(b, index)?;
        }
        0x11 | 0xfe0c => load(b, method.parameters.len() + variable()?)?,
        0x13 | 0xfe0e => store(b, method.parameters.len() + variable()?)?,
        0x15..=0x21 => {
            let (bits, ty) = match i.opcode {
                0x15 => (u64::from(u32::MAX), Type::I32),
                0x16..=0x1e => (u64::from(i.opcode - 0x16), Type::I32),
                _ => match i.operand {
                    Operand::Integer(value) => {
                        if i.opcode == 0x21 {
                            (value as u64, Type::I64)
                        } else {
                            (u64::from(value as u32), Type::I32)
                        }
                    }
                    _ => return Err("Missing integer constant".into()),
                },
            };
            let value = b.value(ty, Op::Constant(bits))?;
            b.stack.push(value);
        }
        0x25 => {
            let value = *b.stack.last().ok_or("CIL evaluation stack underflow")?;
            b.stack.push(value);
        }
        0x26 => {
            b.pop()?;
        }
        0x2a => {
            let value = match method.returns {
                Some(ty) => {
                    let value = b.pop()?;
                    if value.1 != ty {
                        return Err("CIL return type mismatch".into());
                    }
                    Some(value.0)
                }
                None => None,
            };
            if !b.stack.is_empty() {
                return Err("CIL return leaves values on evaluation stack".into());
            }
            return Ok(Control::Return(value));
        }
        0x2b | 0x38 => return Ok(Control::Jump(b.edge(target()?))),
        0x2c..=0x37 | 0x39..=0x44 => {
            let code = if i.opcode >= 0x39 {
                i.opcode - 0x0d
            } else {
                i.opcode
            };
            let (condition, invert) = if code <= 0x2d {
                let value = b.pop()?;
                let zero = b.value(value.1, Op::Constant(0))?;
                (
                    b.value(
                        Type::I32,
                        Op::Compare {
                            op: Comparison::Equal,
                            left: value.0,
                            right: zero.0,
                        },
                    )?,
                    code == 0x2d,
                )
            } else {
                let (op, invert) = match code {
                    0x2e => (Comparison::Equal, false),
                    0x2f => (Comparison::LessSigned, true),
                    0x30 => (Comparison::GreaterSigned, false),
                    0x31 => (Comparison::GreaterSigned, true),
                    0x32 => (Comparison::LessSigned, false),
                    0x33 => (Comparison::Equal, true),
                    0x34 => (Comparison::LessUnsigned, true),
                    0x35 => (Comparison::GreaterUnsigned, false),
                    0x36 => (Comparison::GreaterUnsigned, true),
                    0x37 => (Comparison::LessUnsigned, false),
                    _ => return Err("Invalid conditional branch".into()),
                };
                (b.compare(op)?, invert)
            };
            let (yes, no) = if invert {
                (next()?, target()?)
            } else {
                (target()?, next()?)
            };
            return Ok(Control::Branch {
                condition: condition.0,
                yes: b.edge(yes),
                no: b.edge(no),
            });
        }
        0x46..=0x4d => {
            let (bits, signed, ty) = match i.opcode {
                0x46 => (8, true, Type::I32),
                0x47 => (8, false, Type::I32),
                0x48 => (16, true, Type::I32),
                0x49 => (16, false, Type::I32),
                0x4a => (32, true, Type::I32),
                0x4b => (32, false, Type::I32),
                0x4c => (64, true, Type::I64),
                _ => (16, false, Type::Native),
            };
            let address = b.pop()?;
            if address.1 != Type::Native {
                return Err("Indirect load requires native address".into());
            }
            let value = b.value(
                ty,
                Op::LoadMemory {
                    address: address.0,
                    bits,
                    signed,
                },
            )?;
            b.stack.push(value);
        }
        0x52..=0x55 | 0xdf => {
            let (bits, ty) = match i.opcode {
                0x52 => (8, Type::I32),
                0x53 => (16, Type::I32),
                0x54 => (32, Type::I32),
                0x55 => (64, Type::I64),
                _ => (16, Type::Native),
            };
            let value = b.pop()?;
            let address = b.pop()?;
            if address.1 != Type::Native || value.1 != ty {
                return Err("Indirect store operand type mismatch".into());
            }
            b.instructions.push(Instruction {
                result: None,
                op: Op::StoreMemory {
                    address: address.0,
                    value: value.0,
                    bits,
                },
                il_offset: b.offset,
            });
        }
        0x58..=0x64 => {
            let op = [
                Binary::Add,
                Binary::Subtract,
                Binary::Multiply,
                Binary::DivideSigned,
                Binary::DivideUnsigned,
                Binary::RemainderSigned,
                Binary::RemainderUnsigned,
                Binary::And,
                Binary::Or,
                Binary::Xor,
                Binary::ShiftLeft,
                Binary::ShiftRightSigned,
                Binary::ShiftRightUnsigned,
            ][usize::from(i.opcode - 0x58)];
            b.binary(op)?;
        }
        0x65 | 0x66 => {
            let value = b.pop()?;
            let op = if i.opcode == 0x65 {
                Op::Negate(value.0)
            } else {
                Op::Not(value.0)
            };
            let value = b.value(value.1, op)?;
            b.stack.push(value);
        }
        0x67..=0x6a | 0x6d..=0x6e | 0xd1..=0xd3 | 0xe0 => {
            let (bits, signed, ty) = match i.opcode {
                0x67 => (8, true, Type::I32),
                0x68 => (16, true, Type::I32),
                0x69 => (32, true, Type::I32),
                0x6a => (64, true, Type::I64),
                0x6d => (32, false, Type::I32),
                0x6e => (64, false, Type::I64),
                0xd1 => (16, false, Type::I32),
                0xd2 => (8, false, Type::I32),
                0xd3 => (16, true, Type::Native),
                0xe0 => (16, false, Type::Native),
                _ => return Err("Invalid conversion opcode".into()),
            };
            let value = b.pop()?;
            let value = b.value(
                ty,
                Op::Convert {
                    value: value.0,
                    bits,
                    signed,
                },
            )?;
            b.stack.push(value);
        }
        0xfe01..=0xfe05 => {
            let op = [
                Comparison::Equal,
                Comparison::GreaterSigned,
                Comparison::GreaterUnsigned,
                Comparison::LessSigned,
                Comparison::LessUnsigned,
            ][usize::from(i.opcode - 0xfe01)];
            let value = b.compare(op)?;
            b.stack.push(value);
        }
        opcode => return Err(format!("CIL opcode 0x{opcode:04x} has no lowering yet")),
    }
    Ok(Control::Jump(b.edge(next()?)))
}
