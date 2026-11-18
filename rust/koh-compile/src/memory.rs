use crate::{
    ir::{Function, Op},
    resolve::Definition,
};
use std::collections::{BTreeMap, HashMap, HashSet};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Layout {
    pub heap_bytes: u16,
}

impl Default for Layout {
    fn default() -> Self {
        Self { heap_bytes: 4096 }
    }
}

impl Layout {
    pub fn stack_top(self) -> u16 {
        0xe000 - self.heap_bytes
    }
    pub fn stack_bytes(self) -> u16 {
        self.stack_top() - 0xc100
    }

    pub fn for_program(
        functions: &[Function],
        entry: Definition,
        irq: u16,
    ) -> Result<Self, String> {
        let mut allocations = Vec::new();
        for function in functions {
            let constants: BTreeMap<_, _> = function
                .blocks
                .iter()
                .flat_map(|b| &b.instructions)
                .filter_map(|i| {
                    if let (Some((id, _)), Op::Constant(bits)) = (i.result, &i.op) {
                        Some((id, *bits))
                    } else {
                        None
                    }
                })
                .collect();
            let count = |id: &u32| {
                constants
                    .get(id)
                    .copied()
                    .filter(|&v| v <= u64::from(u16::MAX))
            };
            for instruction in function.blocks.iter().flat_map(|b| &b.instructions) {
                let bytes = match &instruction.op {
                    Op::NewArray {
                        element,
                        length,
                        alignment,
                    } => count(length).map(|n| (n * u64::from(element.bytes()), 2, *alignment)),
                    Op::NewRectangle {
                        element,
                        rows,
                        columns,
                    } => count(rows)
                        .zip(count(columns))
                        .map(|(r, c)| (r * c * u64::from(element.bytes()), 6, 1)),
                    Op::NewObject(class) => Some((u64::from(class.bytes()), 0, 1)),
                    Op::ArenaAllocate(size) => count(size).map(|bytes| (bytes, 0, 1)),
                    _ => None,
                };
                if let Some((bytes, header, alignment)) = bytes {
                    allocations.push((
                        bytes,
                        header,
                        alignment,
                        function.identity,
                        instruction.il_offset,
                    ));
                }
            }
        }
        let mut capacity = 4096u64;
        let mut requirement = 4096;
        let mut origin = (entry, 0);
        while capacity < 0x1f00 {
            let base = 0xe000 - capacity;
            let failure = allocations
                .iter()
                .find_map(|&(bytes, header, alignment, id, il)| {
                    let mask = u64::from(alignment - 1);
                    let payload = (base + header + mask) & !mask;
                    let required = payload + bytes - base;
                    (required > capacity).then_some((required, id, il))
                });
            let Some((bytes, id, il)) = failure else {
                break;
            };
            requirement = bytes;
            origin = (id, il);
            capacity += 256;
        }
        if capacity == 4096 {
            return Ok(Self::default());
        }
        let context = || {
            format!(
                "{} IL_{:04x}: known allocation requires {requirement} bytes",
                crate::sm83::symbol(origin.0),
                origin.1
            )
        };
        if capacity >= 0x1f00 {
            return Err(format!(
                "{}; insufficient physical WRAM for stack and arena",
                context()
            ));
        }
        let result = Self {
            heap_bytes: capacity as u16,
        };
        let indices: HashMap<_, _> = functions
            .iter()
            .enumerate()
            .map(|(i, f)| (f.identity, i))
            .collect();
        let members: HashSet<_> = indices.keys().copied().collect();
        let root = functions
            .iter()
            .find(|f| f.identity == entry)
            .ok_or("RAM layout entry is absent")?;
        let overhead = 6 + root.returns.map_or(0, |ty| u16::from(ty.bits() / 8));
        let need =
            match crate::stack::bound(functions, &indices, &members, &[entry], overhead, "Managed")
                .map_err(|(id, error)| format!("{}: {error}", crate::sm83::symbol(id)))?
            {
                crate::stack::Bound::Finite(bytes) => bytes,
                crate::stack::Bound::Recursive(_) => functions
                    .iter()
                    .map(|f| {
                        let io: usize = f
                            .parameters
                            .iter()
                            .chain(f.returns.iter())
                            .map(|ty| usize::from(ty.bits() / 8))
                            .sum();
                        crate::sm83::frame_size(f).and_then(|frame| {
                            u16::try_from(usize::from(frame) + io + 10)
                                .map_err(|_| "Recursive frame storage overflow".into())
                        })
                    })
                    .collect::<Result<Vec<_>, _>>()?
                    .into_iter()
                    .max()
                    .unwrap_or(0),
            };
        if u32::from(need) + u32::from(irq) > u32::from(result.stack_bytes()) {
            return Err(format!(
                "{}; stack needs {} bytes including IRQ reserve, only {} remain",
                context(),
                u32::from(need) + u32::from(irq),
                result.stack_bytes()
            ));
        }
        Ok(result)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ir::{Block, Control, Instruction, Terminator, Type};
    use rstest::rstest;

    fn allocation(bytes: u64) -> Function {
        Function {
            identity: Definition {
                assembly: 0,
                token: 0x06000001,
            },
            parameters: vec![],
            returns: None,
            locals: vec![],
            blocks: vec![Block {
                parameters: vec![],
                instructions: vec![
                    Instruction {
                        result: Some((0, Type::I32)),
                        op: Op::Constant(bytes),
                        il_offset: 0,
                    },
                    Instruction {
                        result: Some((1, Type::Native)),
                        op: Op::ArenaAllocate(0),
                        il_offset: 7,
                    },
                ],
                terminator: Terminator {
                    kind: Control::Return(None),
                    il_offset: 8,
                },
            }],
        }
    }

    #[rstest]
    #[case(4096, 4096, 0xd000)]
    #[case(4097, 4352, 0xcf00)]
    #[case(5002, 5120, 0xcc00)]
    #[case(5257, 5376, 0xcb00)]
    #[case(7680, 7680, 0xc200)]
    fn partitions_physical_wram(#[case] bytes: u64, #[case] capacity: u16, #[case] top: u16) {
        let f = allocation(bytes);
        let layout = Layout::for_program(std::slice::from_ref(&f), f.identity, 0).unwrap();
        assert_eq!(layout.heap_bytes, capacity);
        assert_eq!(layout.stack_top(), top);
        assert_eq!(layout.stack_bytes() + layout.heap_bytes, 0x1f00);
    }

    #[test]
    fn rejects_impossible_arena_with_member_and_il_offset() {
        let f = allocation(7936);
        let error = Layout::for_program(std::slice::from_ref(&f), f.identity, 0).unwrap_err();
        assert!(error.contains("m06000001 IL_0007"), "{error}");
        assert!(error.contains("insufficient physical WRAM"), "{error}");
    }

    #[test]
    fn reserves_irq_stack_before_accepting_expansion() {
        let f = allocation(5002);
        let error = Layout::for_program(std::slice::from_ref(&f), f.identity, 2816).unwrap_err();
        assert!(error.contains("including IRQ reserve"), "{error}");
        assert!(error.contains("only 2816 remain"), "{error}");
    }

    #[rstest]
    #[case(0, 4096)]
    #[case(1, 4352)]
    #[case(4096, 4352)]
    fn aligns_payload_against_candidate_bus_address(#[case] count: u64, #[case] capacity: u16) {
        use crate::ir::{ArrayKind, Scalar, Storage};
        let mut f = allocation(count);
        let element = Storage::Scalar(Scalar::U8);
        f.blocks[0].instructions[1] = Instruction {
            result: Some((1, Type::Array(element, ArrayKind::Vector))),
            op: Op::NewArray {
                element,
                length: 0,
                alignment: 4096,
            },
            il_offset: 7,
        };
        let layout = Layout::for_program(std::slice::from_ref(&f), f.identity, 0).unwrap();
        assert_eq!(layout.heap_bytes, capacity);
        let payload = (u32::from(layout.stack_top()) + 2 + 4095) & !4095;
        assert!(u64::from(payload) + count <= 0xe000);
    }

    #[rstest]
    #[case(1000, true)]
    #[case(1500, false)]
    fn bounds_nested_frames_against_selected_stack(#[case] bytes: u64, #[case] fits: bool) {
        let mut root = allocation(5002);
        root.blocks[0].instructions.extend([
            Instruction {
                result: Some((2, Type::I32)),
                op: Op::Constant(bytes),
                il_offset: 9,
            },
            Instruction {
                result: Some((3, Type::Native)),
                op: Op::StackAllocate { bytes: 2 },
                il_offset: 10,
            },
        ]);
        let mut child = root.clone();
        child.identity.token += 1;
        root.blocks[0].instructions.push(Instruction {
            result: None,
            op: Op::Call {
                target: child.identity,
                signature: crate::ir::Signature {
                    parameters: vec![],
                    returns: None,
                },
                arguments: vec![],
            },
            il_offset: 11,
        });
        let id = root.identity;
        let result = Layout::for_program(&[root, child], id, 0);
        if fits {
            assert_eq!(result.unwrap().stack_bytes(), 2816);
        } else {
            let error = result.unwrap_err();
            assert!(error.contains("only 2816 remain"), "{error}");
        }
    }
}
