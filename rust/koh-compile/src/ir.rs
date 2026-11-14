use crate::resolve::Definition;
use std::collections::HashMap;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Type {
    I32,
    I64,
    Native,
}

impl Type {
    pub fn bits(self) -> u8 {
        match self {
            Self::I32 => 32,
            Self::I64 => 64,
            Self::Native => 16,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Binary {
    Add,
    Subtract,
    Multiply,
    DivideSigned,
    DivideUnsigned,
    RemainderSigned,
    RemainderUnsigned,
    And,
    Or,
    Xor,
    ShiftLeft,
    ShiftRightSigned,
    ShiftRightUnsigned,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Comparison {
    Equal,
    LessSigned,
    LessUnsigned,
    GreaterSigned,
    GreaterUnsigned,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Signature {
    pub parameters: Vec<Type>,
    pub returns: Option<Type>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Op {
    Call {
        target: Definition,
        signature: Signature,
        arguments: Vec<u32>,
    },
    Constant(u64),
    Binary {
        op: Binary,
        left: u32,
        right: u32,
    },
    Compare {
        op: Comparison,
        left: u32,
        right: u32,
    },
    Negate(u32),
    Not(u32),
    Convert {
        value: u32,
        bits: u8,
        signed: bool,
    },
    LoadLocal(u32),
    StoreLocal {
        local: u32,
        value: u32,
    },
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Instruction {
    pub result: Option<(u32, Type)>,
    pub op: Op,
    pub il_offset: u32,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Edge {
    pub block: usize,
    pub arguments: Vec<u32>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Control {
    Jump(Edge),
    Branch { condition: u32, yes: Edge, no: Edge },
    Return(Option<u32>),
}

impl Control {
    fn edges(&self) -> impl Iterator<Item = &Edge> {
        let (first, second) = match self {
            Self::Jump(edge) => (Some(edge), None),
            Self::Branch { yes, no, .. } => (Some(yes), Some(no)),
            Self::Return(_) => (None, None),
        };
        first.into_iter().chain(second)
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Terminator {
    pub kind: Control,
    pub il_offset: u32,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Block {
    pub parameters: Vec<(u32, Type)>,
    pub instructions: Vec<Instruction>,
    pub terminator: Terminator,
}

#[derive(Debug, Clone)]
pub struct Function {
    pub identity: Definition,
    pub parameters: Vec<Type>,
    pub returns: Option<Type>,
    pub locals: Vec<Type>,
    pub blocks: Vec<Block>,
}

#[derive(Clone, Copy)]
struct Def {
    ty: Type,
    block: usize,
    position: usize,
}

fn dominators(blocks: &[Block], predecessors: &[Vec<usize>]) -> Result<Vec<usize>, String> {
    let mut seen = vec![false; blocks.len()];
    seen[0] = true;
    let mut stack = vec![(0, 0)];
    let mut order = Vec::with_capacity(blocks.len());
    while let Some((block, next)) = stack.last_mut() {
        if let Some(edge) = blocks[*block].terminator.kind.edges().nth(*next) {
            *next += 1;
            if !seen[edge.block] {
                seen[edge.block] = true;
                stack.push((edge.block, 0));
            }
        } else {
            order.push(*block);
            stack.pop();
        }
    }
    if order.len() != blocks.len() {
        return Err("Unreachable IR block".into());
    }
    order.reverse();
    let mut rank = vec![0; blocks.len()];
    for (index, &block) in order.iter().enumerate() {
        rank[block] = index;
    }
    let mut parent = vec![usize::MAX; blocks.len()];
    parent[0] = 0;
    let mut changed = true;
    while changed {
        changed = false;
        for &block in &order[1..] {
            let mut incoming = predecessors[block]
                .iter()
                .copied()
                .filter(|&p| parent[p] != usize::MAX);
            let Some(mut common) = incoming.next() else {
                continue;
            };
            for mut other in incoming {
                while common != other {
                    if rank[common] > rank[other] {
                        common = parent[common];
                    } else {
                        other = parent[other];
                    }
                }
            }
            if parent[block] != common {
                parent[block] = common;
                changed = true;
            }
        }
    }
    Ok(parent)
}

pub fn verify(function: &Function) -> Result<(), String> {
    verify_inner(function).map_err(|error| {
        format!(
            "Assembly {} method 0x{:08x}: {error}",
            function.identity.assembly, function.identity.token
        )
    })
}

pub fn verify_program(functions: &[Function]) -> Result<(), String> {
    if functions.is_empty() || functions.len() > 4096 {
        return Err("Invalid IR program function count".into());
    }
    let mut definitions = HashMap::new();
    for function in functions {
        verify(function)?;
        if definitions.insert(function.identity, function).is_some() {
            return Err("Duplicate IR function identity".into());
        }
    }
    for function in functions {
        for block in &function.blocks {
            for instruction in &block.instructions {
                if let Op::Call {
                    target, signature, ..
                } = &instruction.op
                {
                    let callee = definitions.get(target).ok_or_else(|| {
                        format!(
                            "Method 0x{:08x} IL_{:04x}: missing call target {target:?}",
                            function.identity.token, instruction.il_offset
                        )
                    })?;
                    if signature.parameters != callee.parameters
                        || signature.returns != callee.returns
                    {
                        return Err(format!(
                            "Method 0x{:08x} IL_{:04x}: call signature differs from target {target:?}",
                            function.identity.token, instruction.il_offset
                        ));
                    }
                }
            }
        }
    }
    Ok(())
}

fn verify_inner(function: &Function) -> Result<(), String> {
    let blocks = &function.blocks;
    if blocks.is_empty() || blocks.len() > 16_384 || function.locals.len() > 1024 {
        return Err("Invalid IR block/local count".into());
    }
    if !blocks[0]
        .parameters
        .iter()
        .map(|p| p.1)
        .eq(function.parameters.iter().copied())
    {
        return Err("Entry block parameters differ from function signature".into());
    }
    let count: usize = blocks
        .iter()
        .map(|b| b.parameters.len() + b.instructions.len())
        .sum();
    if count > 262_144 {
        return Err("IR exceeds value/instruction limit".into());
    }
    let mut definitions = HashMap::with_capacity(count);
    let mut predecessors = vec![Vec::new(); blocks.len()];
    let mut edge_arguments = 0usize;
    for (block, body) in blocks.iter().enumerate() {
        for (position, result) in body.parameters.iter().copied().map(|p| (0, Some(p))).chain(
            body.instructions
                .iter()
                .enumerate()
                .map(|(i, op)| (i + 1, op.result)),
        ) {
            if let Some((value, ty)) = result {
                if definitions
                    .insert(
                        value,
                        Def {
                            ty,
                            block,
                            position,
                        },
                    )
                    .is_some()
                {
                    return Err(format!("Block {block}: duplicate value %{value}"));
                }
            }
        }
        for edge in body.terminator.kind.edges() {
            if edge.block == 0 || edge.block >= blocks.len() {
                return Err(format!("Block {block}: invalid edge target {}", edge.block));
            }
            edge_arguments += edge.arguments.len();
            if edge_arguments > 1_048_576 {
                return Err("IR exceeds edge argument limit".into());
            }
            predecessors[edge.block].push(block);
        }
    }
    let parents = dominators(blocks, &predecessors)?;
    let mut initialized = vec![false; function.locals.len()];
    for (block, body) in blocks.iter().enumerate() {
        let check = |value: u32, position: usize| -> Result<Type, String> {
            let def = definitions
                .get(&value)
                .ok_or_else(|| format!("Undefined value %{value}"))?;
            if def.block == block {
                if def.position != 0 && def.position >= position {
                    return Err(format!("Value %{value} used before its definition"));
                }
            } else {
                let mut ancestor = block;
                while ancestor != def.block && ancestor != 0 {
                    ancestor = parents[ancestor];
                }
                if ancestor != def.block {
                    return Err(format!("Value %{value} does not dominate its use"));
                }
            }
            Ok(def.ty)
        };
        let local = |index: u32| {
            function
                .locals
                .get(index as usize)
                .copied()
                .ok_or("Invalid local index".to_string())
        };
        for (index, instruction) in body.instructions.iter().enumerate() {
            let mut checked = || -> Result<(), String> {
                let position = index + 1;
                let result = instruction.result.map(|r| r.1);
                let expected = match instruction.op {
                    Op::Call {
                        ref signature,
                        ref arguments,
                        ..
                    } => {
                        if arguments.len() != signature.parameters.len() || arguments.len() > 1024 {
                            return Err("Invalid call argument count".into());
                        }
                        for (&value, &ty) in arguments.iter().zip(&signature.parameters) {
                            if check(value, position)? != ty {
                                return Err("Call argument type mismatch".into());
                            }
                        }
                        signature.returns
                    }
                    Op::Constant(bits) => {
                        let ty = result.ok_or("Constant has no result")?;
                        if ty.bits() < 64 && bits >= 1u64 << ty.bits() {
                            return Err("Constant exceeds result width".into());
                        }
                        Some(ty)
                    }
                    Op::Binary { op, left, right } => {
                        let left = check(left, position)?;
                        let right = check(right, position)?;
                        let shift = matches!(
                            op,
                            Binary::ShiftLeft
                                | Binary::ShiftRightSigned
                                | Binary::ShiftRightUnsigned
                        );
                        if right != if shift { Type::I32 } else { left } {
                            return Err("Binary operand type mismatch".into());
                        }
                        Some(left)
                    }
                    Op::Compare { left, right, .. } => {
                        if check(left, position)? != check(right, position)? {
                            return Err("Comparison operand type mismatch".into());
                        }
                        Some(Type::I32)
                    }
                    Op::Negate(value) | Op::Not(value) => Some(check(value, position)?),
                    Op::Convert { value, bits, .. } => {
                        check(value, position)?;
                        let ty = result.ok_or("Conversion has no result")?;
                        if !matches!(bits, 8 | 16 | 32 | 64) || bits > ty.bits() {
                            return Err("Invalid conversion width".into());
                        }
                        Some(ty)
                    }
                    Op::LoadLocal(index) => {
                        let ty = local(index)?;
                        if !initialized[index as usize] {
                            return Err("Local load lacks dominating entry initialization".into());
                        }
                        Some(ty)
                    }
                    Op::StoreLocal {
                        local: index,
                        value,
                    } => {
                        if check(value, position)? != local(index)? {
                            return Err("Local store type mismatch".into());
                        }
                        if block == 0 {
                            initialized[index as usize] = true;
                        }
                        None
                    }
                };
                if result != expected {
                    return Err("Operation result type mismatch".into());
                }
                Ok(())
            };
            checked()
                .map_err(|e| format!("Block {block} IL_{:04x}: {e}", instruction.il_offset))?;
        }
        let checked = || -> Result<(), String> {
            let position = body.instructions.len() + 1;
            if let Control::Branch { condition, .. } = body.terminator.kind {
                if check(condition, position)? != Type::I32 {
                    return Err("Branch condition must be int32".into());
                }
            }
            if let Control::Return(value) = body.terminator.kind {
                if value.map(|v| check(v, position)).transpose()? != function.returns {
                    return Err("Return type differs from function signature".into());
                }
            }
            for edge in body.terminator.kind.edges() {
                let parameters = &blocks[edge.block].parameters;
                if edge.arguments.len() != parameters.len() {
                    return Err("Edge argument count differs from block parameters".into());
                }
                for (&argument, &(_, ty)) in edge.arguments.iter().zip(parameters) {
                    if check(argument, position)? != ty {
                        return Err("Edge argument type mismatch".into());
                    }
                }
            }
            Ok(())
        };
        checked()
            .map_err(|e| format!("Block {block} IL_{:04x}: {e}", body.terminator.il_offset))?;
    }
    Ok(())
}
