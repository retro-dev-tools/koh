use crate::{
    ir::{Function, Op},
    resolve::{Definition, Resolver},
};
use std::collections::{BTreeMap, HashMap, HashSet, VecDeque};

#[derive(Default)]
pub(crate) struct Plan {
    pub vectors: BTreeMap<u16, Definition>,
    pub functions: HashSet<Definition>,
    pub shared: HashSet<Definition>,
    pub reserve: u16,
}

fn reachable(
    functions: &[Function],
    indices: &HashMap<Definition, usize>,
    roots: impl IntoIterator<Item = Definition>,
) -> Result<HashSet<Definition>, String> {
    let mut seen = HashSet::new();
    let mut queue: VecDeque<_> = roots.into_iter().collect();
    while let Some(id) = queue.pop_front() {
        if !seen.insert(id) {
            continue;
        }
        let function = &functions[*indices.get(&id).ok_or("Interrupt call target is absent")?];
        for instruction in function.blocks.iter().flat_map(|b| &b.instructions) {
            if let Op::Call { target, .. } | Op::NewDelegate { target, .. } = instruction.op {
                queue.push_back(target);
            }
        }
    }
    Ok(seen)
}

impl Plan {
    pub fn new(
        resolver: &Resolver<'_>,
        functions: &[Function],
        entry: Definition,
        vectors: BTreeMap<u16, Definition>,
    ) -> Result<Self, String> {
        if vectors.is_empty() {
            return Ok(Self::default());
        }
        let indices: HashMap<_, _> = functions
            .iter()
            .enumerate()
            .map(|(i, f)| (f.identity, i))
            .collect();
        let members = reachable(functions, &indices, vectors.values().copied())?;
        let main = reachable(functions, &indices, [entry])?;
        let context = |id, error: String| -> Result<String, String> {
            let origin = resolver
                .instance(id)?
                .map_or(id, |instance| instance.method);
            let name = resolver
                .assembly(origin.assembly)?
                .method_name(origin.token)
                .unwrap_or_else(|_| format!("{origin:?}"));
            Ok(format!("{name}: {error}"))
        };
        for function in functions.iter().filter(|f| members.contains(&f.identity)) {
            if let Err(error) = verify_interrupt(function) {
                return Err(context(function.identity, error)?);
            }
        }
        let reserve = match stack_reserve(functions, &indices, &members, &vectors) {
            Ok(reserve) => reserve,
            Err((id, error)) => return Err(context(id, error)?),
        };
        Ok(Self {
            vectors,
            shared: members.intersection(&main).copied().collect(),
            functions: members,
            reserve,
        })
    }
}

fn stack_reserve(
    functions: &[Function],
    indices: &HashMap<Definition, usize>,
    members: &HashSet<Definition>,
    vectors: &BTreeMap<u16, Definition>,
) -> Result<u16, (Definition, String)> {
    match crate::stack::bound(
        functions,
        indices,
        members,
        &vectors.values().copied().collect::<Vec<_>>(),
        16,
        "Interrupt",
    )? {
        crate::stack::Bound::Finite(reserve) => Ok(reserve),
        crate::stack::Bound::Recursive(member) => {
            Err((member, "Interrupt call graph must be acyclic".into()))
        }
    }
}

fn verify_interrupt(function: &Function) -> Result<(), String> {
    use crate::ir::{Op, Type};
    let scalar = |ty| matches!(ty, Type::I32 | Type::I64 | Type::Native);
    if function
        .parameters
        .iter()
        .chain(function.returns.iter())
        .any(|&ty| !scalar(ty))
    {
        return Err("Interrupt calls require scalar integer parameters and returns".into());
    }
    if function.locals.iter().any(|&ty| !scalar(ty)) {
        return Err("Interrupt leaf handlers require integer locals".into());
    }
    let statics: HashSet<_> = function
        .blocks
        .iter()
        .flat_map(|b| &b.instructions)
        .filter_map(|i| {
            matches!(i.op, Op::StaticAddress { .. })
                .then_some(i.result)
                .flatten()
                .map(|r| r.0)
        })
        .collect();
    let registers: std::collections::HashMap<_, _> = function
        .blocks
        .iter()
        .flat_map(|b| &b.instructions)
        .filter_map(|instruction| match (instruction.result, &instruction.op) {
            (Some((value, Type::Native)), Op::Constant(address)) => Some((value, *address as u16)),
            _ => None,
        })
        .collect();
    let memory = |address, bits, write| {
        statics.contains(&address)
            || (bits == 8
                && registers
                    .get(&address)
                    .is_some_and(|&physical| interrupt_register(physical, write)))
    };
    for block in &function.blocks {
        if block.parameters.iter().any(|&(_, ty)| !scalar(ty)) {
            return Err("Interrupt leaf handlers require integer block parameters".into());
        }
        for instruction in &block.instructions {
            let supported = match instruction.op {
                Op::StaticAddress { .. } => true,
                Op::LoadMemory { address, bits, .. } => memory(address, bits, false),
                Op::StoreMemory { address, bits, .. } => memory(address, bits, true),
                Op::Constant(_)
                | Op::Binary { .. }
                | Op::Compare { .. }
                | Op::RequireRange(_)
                | Op::Negate(_)
                | Op::Not(_)
                | Op::Convert { .. }
                | Op::Call { .. }
                | Op::LoadLocal(_)
                | Op::StoreLocal { .. } => true,
                _ => false,
            };
            if !supported
                || instruction.result.is_some_and(|(_, ty)| {
                    !scalar(ty) && !matches!(instruction.op, Op::StaticAddress { .. })
                })
            {
                return Err(format!(
                    "Interrupt leaf handler operation unsupported at IL_{:04x}: {:?}",
                    instruction.il_offset, instruction.op
                ));
            }
        }
    }
    Ok(())
}

fn interrupt_register(address: u16, write: bool) -> bool {
    matches!(address, 0xff00..=0xff7f | 0xffff)
        && (!write || !matches!(address, 0xff46 | 0xff4d | 0xff4f | 0xff50 | 0xff55 | 0xff70))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ir::*;

    #[rstest::rstest]
    #[case(Type::Native, 8, true)]
    #[case(Type::I32, 8, true)]
    #[case(Type::I64, 8, true)]
    #[case(Type::I32, 256, false)]
    #[case(Type::I64, 128, false)]
    fn interrupt_stack_budget_includes_nested_call_storage(
        #[case] ty: Type,
        #[case] count: u32,
        #[case] fits: bool,
    ) {
        let id = |index: u32| Definition {
            assembly: 0,
            token: 0x06000001 + index,
        };
        let signature = Signature {
            parameters: vec![ty; count as usize],
            returns: Some(ty),
        };
        let mut functions = vec![Function {
            identity: id(0),
            parameters: vec![],
            returns: None,
            locals: vec![],
            blocks: vec![Block {
                parameters: vec![],
                instructions: vec![
                    Instruction {
                        result: Some((0, ty)),
                        op: Op::Constant(1),
                        il_offset: 0,
                    },
                    Instruction {
                        result: Some((1, ty)),
                        op: Op::Call {
                            target: id(1),
                            signature: signature.clone(),
                            arguments: vec![0; count as usize],
                        },
                        il_offset: 1,
                    },
                ],
                terminator: Terminator {
                    kind: Control::Return(None),
                    il_offset: 2,
                },
            }],
        }];
        for index in 1..=4 {
            let instructions = if index == 4 {
                vec![]
            } else {
                vec![Instruction {
                    result: Some((count, ty)),
                    op: Op::Call {
                        target: id(index + 1),
                        signature: signature.clone(),
                        arguments: vec![0; count as usize],
                    },
                    il_offset: 0,
                }]
            };
            functions.push(Function {
                identity: id(index),
                parameters: signature.parameters.clone(),
                returns: Some(ty),
                locals: vec![],
                blocks: vec![Block {
                    parameters: (0..count).map(|value| (value, ty)).collect(),
                    instructions,
                    terminator: Terminator {
                        kind: Control::Return(Some(if index == 4 { 0 } else { count })),
                        il_offset: 1,
                    },
                }],
            });
        }
        verify_program(&functions).unwrap();
        let indices = functions
            .iter()
            .enumerate()
            .map(|(index, f)| (f.identity, index))
            .collect();
        let members = functions.iter().map(|f| f.identity).collect();
        let result = stack_reserve(
            &functions,
            &indices,
            &members,
            &BTreeMap::from([(0x40, id(0))]),
        );
        if fits {
            assert!(result.unwrap() <= 0xf00);
        } else {
            assert!(result.unwrap_err().1.contains("capacity"));
        }
    }
}
