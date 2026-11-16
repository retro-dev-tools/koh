use crate::resolve::Definition;
use std::collections::HashMap;
mod escape;
mod pins;
mod stack;
pub(crate) use stack::allocations as stack_allocations;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct ValueType {
    identity: Definition,
    bytes: u16,
    references: bool,
}

impl ValueType {
    pub fn new(identity: Definition, bytes: u16) -> Result<Self, String> {
        if identity.token >> 24 != 2 || identity.token & 0xffffff == 0 {
            return Err("Value type requires a TypeDef identity".into());
        }
        if !(1..=4096).contains(&bytes) {
            return Err("Value type storage must be 1 through 4096 bytes".into());
        }
        Ok(Self {
            identity,
            bytes,
            references: false,
        })
    }

    pub fn with_references(identity: Definition, bytes: u16) -> Result<Self, String> {
        let mut value = Self::new(identity, bytes)?;
        value.references = true;
        Ok(value)
    }

    pub fn contains_references(self) -> bool {
        self.references
    }

    pub fn identity(self) -> Definition {
        self.identity
    }
    pub fn bytes(self) -> u16 {
        self.bytes
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct ClassType {
    identity: Option<Definition>,
    base: Option<Definition>,
    bytes: u16,
}

impl ClassType {
    pub fn root() -> Self {
        Self {
            identity: None,
            base: None,
            bytes: 2,
        }
    }
    pub fn new(identity: Definition, base: Option<Definition>, bytes: u16) -> Result<Self, String> {
        if std::iter::once(identity)
            .chain(base)
            .any(|id| id.token >> 24 != 2 || id.token & 0xffffff == 0)
            || base == Some(identity)
            || !(2..=4096).contains(&bytes)
        {
            return Err(
                "Class type requires valid TypeDef identities and2..4096-byte storage".into(),
            );
        }
        Ok(Self {
            identity: Some(identity),
            base,
            bytes,
        })
    }
    pub fn identity(self) -> Option<Definition> {
        self.identity
    }
    pub fn base(self) -> Option<Definition> {
        self.base
    }
    pub fn bytes(self) -> u16 {
        self.bytes
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Type {
    Object(ClassType),
    ReferenceCell(ReferenceCell),
    I32,
    I64,
    Native,
    ByRef(Scalar),
    Array(Storage, ArrayKind),
    String,
    Null,
    FieldHandle(Definition),
    Struct(ValueType),
    StructRef(ValueType),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ReferenceCell {
    Object(ClassType),
    String,
    Array(Storage, ArrayKind),
}

impl ReferenceCell {
    pub fn stack_type(self) -> Type {
        match self {
            Self::Object(class) => Type::Object(class),
            Self::String => Type::String,
            Self::Array(element, kind) => Type::Array(element, kind),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ArrayKind {
    Vector,
    Rectangle,
}

impl ArrayKind {
    pub fn rank(self) -> u16 {
        match self {
            Self::Vector => 1,
            Self::Rectangle => 2,
        }
    }
    pub fn header_bytes(self) -> u16 {
        2 + if self == Self::Rectangle { 4 } else { 0 }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Scalar {
    Bool,
    Char,
    I8,
    U8,
    I16,
    U16,
    I32,
    U32,
    I64,
    U64,
    NativeSigned,
    NativeUnsigned,
}

impl Scalar {
    pub(crate) fn signed(self) -> bool {
        matches!(
            self,
            Self::I8 | Self::I16 | Self::I32 | Self::I64 | Self::NativeSigned
        )
    }
    pub(crate) fn stack_type(self) -> Type {
        match self {
            Self::I64 | Self::U64 => Type::I64,
            Self::NativeSigned | Self::NativeUnsigned => Type::Native,
            _ => Type::I32,
        }
    }
    pub fn bits(self) -> u8 {
        match self {
            Self::Bool | Self::I8 | Self::U8 => 8,
            Self::Char | Self::I16 | Self::U16 | Self::NativeSigned | Self::NativeUnsigned => 16,
            Self::I32 | Self::U32 => 32,
            Self::I64 | Self::U64 => 64,
        }
    }
}

impl Type {
    pub(crate) fn class(self) -> Option<ClassType> {
        match self {
            Self::Object(class)
            | Self::ReferenceCell(ReferenceCell::Object(class))
            | Self::Array(Storage::Object(class), _)
            | Self::ReferenceCell(ReferenceCell::Array(Storage::Object(class), _)) => Some(class),
            _ => None,
        }
    }
    pub(crate) fn is_byref(self) -> bool {
        matches!(
            self,
            Self::ByRef(_) | Self::StructRef(_) | Self::ReferenceCell(_)
        )
    }
    pub(crate) fn addresses(self, bits: u16) -> bool {
        self == Self::Native
            || matches!(self, Self::ByRef(scalar) if u16::from(scalar.bits()) == bits)
            || matches!(self, Self::StructRef(value) if value.bytes * 8 == bits)
            || matches!(self, Self::ReferenceCell(_) if bits == 16)
    }
    pub fn bits(self) -> u16 {
        match self {
            Self::Struct(value) => value.bytes * 8,
            Self::StructRef(_) => 16,
            Self::FieldHandle(_) => 0,
            Self::I32 => 32,
            Self::I64 => 64,
            Self::Native
            | Self::ByRef(_)
            | Self::Array(_, _)
            | Self::String
            | Self::Null
            | Self::Object(_)
            | Self::ReferenceCell(_) => 16,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Storage {
    Object(ClassType),
    String,
    Scalar(Scalar),
    Struct(ValueType),
}

impl Storage {
    pub fn bytes(self) -> u16 {
        match self {
            Self::Object(_) | Self::String => 2,
            Self::Scalar(value) => u16::from(value.bits() / 8),
            Self::Struct(value) => value.bytes,
        }
    }
    pub fn stack_type(self) -> Type {
        match self {
            Self::Object(class) => Type::Object(class),
            Self::String => Type::String,
            Self::Scalar(value) => value.stack_type(),
            Self::Struct(value) => Type::Struct(value),
        }
    }
    pub fn reference_type(self) -> Type {
        match self {
            Self::Object(class) => Type::ReferenceCell(ReferenceCell::Object(class)),
            Self::String => Type::ReferenceCell(ReferenceCell::String),
            Self::Scalar(value) => Type::ByRef(value),
            Self::Struct(value) => Type::StructRef(value),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FieldStorage {
    Value(Storage),
    Array(Storage, ArrayKind),
}

impl FieldStorage {
    pub fn contains_references(self) -> bool {
        match self {
            Self::Array(_, _) | Self::Value(Storage::Object(_) | Storage::String) => true,
            Self::Value(Storage::Struct(value)) => value.references,
            Self::Value(Storage::Scalar(_)) => false,
        }
    }
    pub fn bytes(self) -> u16 {
        match self {
            Self::Value(storage) => storage.bytes(),
            Self::Array(_, _) => 2,
        }
    }
    pub fn stack_type(self) -> Type {
        match self {
            Self::Value(storage) => storage.stack_type(),
            Self::Array(element, kind) => Type::Array(element, kind),
        }
    }
    pub fn reference_type(self) -> Type {
        match self {
            Self::Value(storage) => storage.reference_type(),
            Self::Array(element, kind) => Type::ReferenceCell(ReferenceCell::Array(element, kind)),
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

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CpuControl {
    DisableInterrupts,
    EnableInterrupts,
    Halt,
    Nop,
    Stop,
}

pub(crate) fn is_hblank_register(register: u16) -> bool {
    matches!(register, 0xff42..=0xff43 | 0xff47..=0xff4b)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Op {
    IsClass {
        object: u32,
        class: ClassType,
    },
    CastClass {
        object: u32,
        class: ClassType,
    },
    NewObject(ClassType),
    Upcast {
        value: u32,
        class: ClassType,
    },
    CheckObject(u32),
    ObjectFieldAddress {
        object: u32,
        owner: ClassType,
        offset: u16,
        storage: FieldStorage,
    },
    HBlankStream {
        source: u32,
        first: u32,
        count: u32,
        register: u16,
    },
    ArenaAllocate(u32),
    ArenaReset,
    MemoryFill {
        address: u32,
        value: u32,
        count: u32,
    },
    MemoryCopy {
        destination: u32,
        source: u32,
        count: u32,
    },
    VBlankCopy {
        destination: u32,
        source: u32,
        count: u32,
    },
    CpuControl(CpuControl),
    OamDma(u32),
    StringLiteral(Vec<u16>),
    StringLength(u32),
    StringChar {
        string: u32,
        index: u32,
    },
    ZeroStruct,
    FieldAddress {
        address: u32,
        owner: ValueType,
        offset: u16,
        storage: FieldStorage,
    },
    ExtractField {
        value: u32,
        offset: u16,
        storage: FieldStorage,
    },
    StackAllocate {
        bytes: u32,
    },
    FieldHandle(Definition),
    InitializeArray {
        array: u32,
        bytes: Vec<u8>,
    },
    NewArray {
        element: Storage,
        length: u32,
        alignment: u16,
    },
    NewRectangle {
        element: Storage,
        rows: u32,
        columns: u32,
    },
    RectangleAddress {
        array: u32,
        row: u32,
        column: u32,
    },
    ArrayDimension {
        array: u32,
        dimension: u32,
    },
    ArrayLength(u32),
    ArrayAddress {
        array: u32,
        index: u32,
    },
    LocalAddress {
        local: u32,
        storage: FieldStorage,
    },
    InitializerAddress(Definition),
    StaticAddress {
        field: Definition,
        bytes: u16,
        alignment: u16,
    },
    LoadMemory {
        address: u32,
        bits: u16,
        signed: bool,
    },
    StoreMemory {
        address: u32,
        value: u32,
        bits: u16,
    },
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
    StorePinned {
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

impl Op {
    pub(crate) fn class_types(&self) -> impl Iterator<Item = ClassType> {
        match *self {
            Self::NewObject(class)
            | Self::Upcast { class, .. }
            | Self::IsClass { class, .. }
            | Self::CastClass { class, .. }
            | Self::ObjectFieldAddress { owner: class, .. } => Some(class),
            _ => None,
        }
        .into_iter()
    }
    fn value_types(&self) -> impl Iterator<Item = ValueType> {
        let (owner, storage) = match *self {
            Self::FieldAddress { owner, storage, .. } => (Some(owner), Some(storage)),
            Self::ExtractField { storage, .. } | Self::LocalAddress { storage, .. } => {
                (None, Some(storage))
            }
            Self::NewArray {
                element: storage, ..
            }
            | Self::NewRectangle {
                element: storage, ..
            } => (None, Some(FieldStorage::Value(storage))),
            _ => (None, None),
        };
        [
            owner,
            match storage {
                Some(
                    FieldStorage::Value(Storage::Struct(value))
                    | FieldStorage::Array(Storage::Struct(value), _),
                ) => Some(value),
                _ => None,
            },
        ]
        .into_iter()
        .flatten()
    }
    pub fn operands(&self) -> impl Iterator<Item = u32> + '_ {
        let pair = match *self {
            Self::NewRectangle { rows, columns, .. } => [Some(rows), Some(columns)],
            Self::RectangleAddress { array, row, .. } => [Some(array), Some(row)],
            Self::ArrayDimension { array, dimension } => [Some(array), Some(dimension)],
            Self::ArenaAllocate(bytes)
            | Self::StackAllocate { bytes }
            | Self::OamDma(bytes)
            | Self::CheckObject(bytes)
            | Self::IsClass { object: bytes, .. }
            | Self::CastClass { object: bytes, .. }
            | Self::ObjectFieldAddress { object: bytes, .. }
            | Self::Upcast { value: bytes, .. } => [Some(bytes), None],
            Self::InitializeArray { array, .. } => [Some(array), None],
            Self::NewArray { length, .. }
            | Self::ArrayLength(length)
            | Self::StringLength(length) => [Some(length), None],
            Self::ArrayAddress { array, index } => [Some(array), Some(index)],
            Self::StringChar { string, index } => [Some(string), Some(index)],
            Self::LoadMemory { address, .. } | Self::FieldAddress { address, .. } => {
                [Some(address), None]
            }
            Self::ExtractField { value, .. } => [Some(value), None],
            Self::StoreMemory { address, value, .. }
            | Self::MemoryFill { address, value, .. }
            | Self::HBlankStream {
                source: address,
                first: value,
                ..
            }
            | Self::MemoryCopy {
                destination: address,
                source: value,
                ..
            }
            | Self::VBlankCopy {
                destination: address,
                source: value,
                ..
            } => [Some(address), Some(value)],
            Self::Binary { left, right, .. } | Self::Compare { left, right, .. } => {
                [Some(left), Some(right)]
            }
            Self::Negate(value)
            | Self::Not(value)
            | Self::Convert { value, .. }
            | Self::StoreLocal { value, .. }
            | Self::StorePinned { value, .. } => [Some(value), None],
            Self::ArenaReset
            | Self::NewObject(_)
            | Self::CpuControl(_)
            | Self::Call { .. }
            | Self::StringLiteral(_)
            | Self::ZeroStruct
            | Self::FieldHandle(_)
            | Self::InitializerAddress(_)
            | Self::Constant(_)
            | Self::StaticAddress { .. }
            | Self::LocalAddress { .. }
            | Self::LoadLocal(_) => [None, None],
        };
        let arguments = match self {
            Self::Call { arguments, .. } => arguments.as_slice(),
            _ => &[],
        };
        let count = match *self {
            Self::RectangleAddress { column, .. } => Some(column),
            Self::MemoryFill { count, .. }
            | Self::MemoryCopy { count, .. }
            | Self::VBlankCopy { count, .. }
            | Self::HBlankStream { count, .. } => Some(count),
            _ => None,
        };
        pair.into_iter()
            .flatten()
            .chain(count)
            .chain(arguments.iter().copied())
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Edge {
    pub block: usize,
    pub arguments: Vec<u32>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Control {
    TypeFault,
    Jump(Edge),
    Branch {
        condition: u32,
        yes: Edge,
        no: Edge,
    },
    Switch {
        selector: u32,
        cases: Vec<Edge>,
        default: Edge,
    },
    Return(Option<u32>),
}

impl Control {
    pub(crate) fn edges(&self) -> impl Iterator<Item = &Edge> {
        let (first, second, cases): (_, _, &[Edge]) = match self {
            Self::Jump(edge) => (Some(edge), None, &[]),
            Self::Branch { yes, no, .. } => (Some(yes), Some(no), &[]),
            Self::Switch { cases, default, .. } => (Some(default), None, cases),
            Self::Return(_) | Self::TypeFault => (None, None, &[]),
        };
        first.into_iter().chain(second).chain(cases)
    }

    pub(crate) fn edges_mut(&mut self) -> impl Iterator<Item = &mut Edge> {
        let (first, second, cases): (_, _, &mut [Edge]) = match self {
            Self::Jump(edge) => (Some(edge), None, &mut []),
            Self::Branch { yes, no, .. } => (Some(yes), Some(no), &mut []),
            Self::Switch { cases, default, .. } => (Some(default), None, cases),
            Self::Return(_) | Self::TypeFault => (None, None, &mut []),
        };
        first.into_iter().chain(second).chain(cases)
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
    let mut aggregates = HashMap::new();
    static_fields(functions)?;
    for function in functions {
        verify(function)?;
        aggregate_types(function, &mut aggregates)?;
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
    pins::verify_banks(functions)
}

pub fn static_fields(functions: &[Function]) -> Result<Vec<(Definition, u16, u16)>, String> {
    let mut fields = std::collections::BTreeMap::new();
    let mut references = HashMap::new();
    for instruction in functions
        .iter()
        .flat_map(|f| &f.blocks)
        .flat_map(|b| &b.instructions)
    {
        let storage = match instruction.op {
            Op::StaticAddress {
                field,
                bytes,
                alignment,
            } => Some((field, bytes, alignment)),
            Op::InitializerAddress(constructor) => Some((constructor, 1, 1)),
            _ => None,
        };
        if let Some((field, bytes, alignment)) = storage {
            let cell = instruction.result.and_then(|(_, ty)| match ty {
                Type::ReferenceCell(cell) => Some(cell),
                _ => None,
            });
            if references
                .insert(field, cell)
                .is_some_and(|old| old != cell)
            {
                return Err(format!(
                    "Conflicting static reference storage for {field:?}"
                ));
            }
            if fields
                .insert((field.assembly, field.token), (bytes, alignment))
                .is_some_and(|old| old != (bytes, alignment))
            {
                return Err(format!("Conflicting static field widths for {field:?}"));
            }
        }
    }
    Ok(fields
        .into_iter()
        .map(|((assembly, token), (bytes, alignment))| {
            (Definition { assembly, token }, bytes, alignment)
        })
        .collect())
}

fn aggregate_types(
    function: &Function,
    sizes: &mut HashMap<Definition, Type>,
) -> Result<(), String> {
    for ty in function
        .parameters
        .iter()
        .chain(&function.locals)
        .chain(function.returns.iter())
        .copied()
        .chain(function.blocks.iter().flat_map(|block| {
            block.parameters.iter().map(|p| p.1).chain(
                block
                    .instructions
                    .iter()
                    .filter_map(|i| i.result.map(|result| result.1)),
            )
        }))
        .chain(function.blocks.iter().flat_map(|block| {
            block
                .instructions
                .iter()
                .flat_map(|i| i.op.value_types().map(Type::Struct))
        }))
        .chain(function.blocks.iter().flat_map(|b| {
            b.instructions
                .iter()
                .flat_map(|i| i.op.class_types().map(Type::Object))
        }))
    {
        let storage = match ty {
            Type::Struct(value)
            | Type::StructRef(value)
            | Type::Array(Storage::Struct(value), _)
            | Type::ReferenceCell(ReferenceCell::Array(Storage::Struct(value), _)) => {
                Some((value.identity, Type::Struct(value)))
            }
            _ => ty
                .class()
                .and_then(|class| class.identity().map(|id| (id, Type::Object(class)))),
        };
        if let Some((identity, storage)) = storage {
            if sizes
                .insert(identity, storage)
                .is_some_and(|old| old != storage)
            {
                return Err(format!(
                    "Conflicting aggregate storage sizes or class descriptors for {identity:?}"
                ));
            }
        }
    }
    Ok(())
}

fn verify_inner(function: &Function) -> Result<(), String> {
    aggregate_types(function, &mut HashMap::new())?;
    if function
        .parameters
        .iter()
        .chain(&function.locals)
        .chain(function.returns.iter())
        .any(|ty| matches!(ty, Type::Null | Type::FieldHandle(_)))
    {
        return Err(
            "Null/metadata handles are evaluation-stack types, not declared storage".into(),
        );
    }
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
    let mut edge_count = 0usize;
    for body in blocks {
        if matches!(&body.terminator.kind, Control::Switch { cases, .. } if cases.len() > 16_384) {
            return Err(format!(
                "IL_{:04x}: Switch case count exceeds limit",
                body.terminator.il_offset
            ));
        }
        edge_count += body.terminator.kind.edges().count();
        if edge_count > 262_144 {
            return Err("IR exceeds control-flow edge limit".into());
        }
    }
    let data_bytes = blocks
        .iter()
        .flat_map(|b| &b.instructions)
        .try_fold(0usize, |total, i| {
            total.checked_add(match &i.op {
                Op::InitializeArray { bytes, .. } => bytes.len(),
                Op::StringLiteral(units) => units.len() * 2 + 2,
                _ => 0,
            })
        })
        .ok_or("IR initializer size overflow")?;
    if data_bytes > 65536 {
        return Err("IR initializer data exceeds 64 KiB budget".into());
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
    let pinned: std::collections::HashSet<_> = blocks
        .iter()
        .flat_map(|b| &b.instructions)
        .filter_map(|i| match i.op {
            Op::StorePinned { local, .. } => Some(local),
            _ => None,
        })
        .collect();
    let zeroes: std::collections::HashSet<_> = blocks
        .iter()
        .flat_map(|b| &b.instructions)
        .filter_map(|i| match (i.result, &i.op) {
            (Some((id, _)), Op::Constant(0)) => Some(id),
            _ => None,
        })
        .collect();
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
                    Op::IsClass { object, .. } | Op::CastClass { object, .. } => {
                        if !matches!(check(object, position)?, Type::Object(_) | Type::Null) {
                            return Err("Class operation requires an object reference".into());
                        }
                        Some(match instruction.op {
                            Op::CastClass { class, .. } => Type::Object(class),
                            _ => Type::I32,
                        })
                    }
                    Op::NewObject(class) => Some(Type::Object(class)),
                    Op::Upcast { value, class } => {
                        let Type::Object(source) = check(value, position)? else {
                            return Err("Upcast requires an object reference".into());
                        };
                        if source.identity().is_none()
                            || source.base() != class.identity()
                            || source.bytes() < class.bytes()
                        {
                            return Err("Upcast must name the immediate base class".into());
                        }
                        Some(Type::Object(class))
                    }
                    Op::CheckObject(value) => {
                        if !matches!(check(value, position)?, Type::Object(_)) {
                            return Err("Object null check requires object reference".into());
                        }
                        None
                    }
                    Op::ObjectFieldAddress {
                        object,
                        owner,
                        offset,
                        storage,
                    } => {
                        if check(object, position)? != Type::Object(owner)
                            || offset < 2
                            || offset
                                .checked_add(storage.bytes())
                                .is_none_or(|end| end > owner.bytes())
                        {
                            return Err("Object field address owner/extent mismatch".into());
                        }
                        Some(storage.reference_type())
                    }
                    Op::ZeroStruct => {
                        if !matches!(result, Some(Type::Struct(_))) {
                            return Err("ZeroStruct requires aggregate result".into());
                        }
                        result
                    }
                    Op::FieldAddress {
                        address,
                        owner,
                        offset,
                        storage,
                    } => {
                        let pointer = check(address, position)?;
                        if storage.contains_references() && !owner.references {
                            return Err(
                                "Reference field requires a reference-bearing struct layout".into(),
                            );
                        }
                        if owner.references && pointer == Type::Native {
                            return Err(
                                "Reference-bearing struct fields require a typed struct reference"
                                    .into(),
                            );
                        }
                        if pointer != Type::Native && pointer != Type::StructRef(owner) {
                            return Err("Field address owner type mismatch".into());
                        }
                        if offset
                            .checked_add(storage.bytes())
                            .is_none_or(|end| end > owner.bytes)
                        {
                            return Err("Field address exceeds aggregate storage".into());
                        }
                        Some(if pointer == Type::Native {
                            Type::Native
                        } else {
                            storage.reference_type()
                        })
                    }
                    Op::ExtractField {
                        value,
                        offset,
                        storage,
                    } => {
                        let Type::Struct(owner) = check(value, position)? else {
                            return Err("Field extraction requires aggregate value".into());
                        };
                        if storage.contains_references() && !owner.references {
                            return Err(
                                "Reference extraction requires a reference-bearing struct layout"
                                    .into(),
                            );
                        }
                        if offset
                            .checked_add(storage.bytes())
                            .is_none_or(|end| end > owner.bytes)
                        {
                            return Err("Field extraction exceeds aggregate storage".into());
                        }
                        Some(storage.stack_type())
                    }
                    Op::ArenaReset | Op::CpuControl(_) => None,
                    Op::HBlankStream {
                        source,
                        first,
                        count,
                        register,
                    } => {
                        if check(source, position)? != Type::Native
                            || check(first, position)? != Type::I32
                            || check(count, position)? != Type::I32
                            || !is_hblank_register(register)
                        {
                            return Err("HBlank stream requires native source, int32 line/count and a display register".into());
                        }
                        None
                    }
                    Op::MemoryFill {
                        address,
                        value,
                        count,
                    } => {
                        if check(address, position)? != Type::Native
                            || check(value, position)? != Type::I32
                            || check(count, position)? != Type::I32
                        {
                            return Err(
                                "Memory fill requires native address and int32 byte/count storage"
                                    .into(),
                            );
                        }
                        None
                    }
                    Op::MemoryCopy {
                        destination,
                        source,
                        count,
                    }
                    | Op::VBlankCopy {
                        destination,
                        source,
                        count,
                    } => {
                        if check(destination, position)? != Type::Native
                            || check(source, position)? != Type::Native
                            || check(count, position)? != Type::I32
                        {
                            return Err(
                                "Memory copy requires native addresses and int32 count storage"
                                    .into(),
                            );
                        }
                        if matches!(instruction.op, Op::VBlankCopy { .. }) {
                            Some(Type::I32)
                        } else {
                            None
                        }
                    }
                    Op::ArenaAllocate(bytes) => {
                        if check(bytes, position)? != Type::I32 {
                            return Err("Raw arena size must be int32".into());
                        }
                        Some(Type::Native)
                    }
                    Op::OamDma(page) => {
                        if check(page, position)? != Type::I32 {
                            return Err("OAM DMA page requires int32 byte storage".into());
                        }
                        None
                    }
                    Op::StackAllocate { bytes } => {
                        if !matches!(check(bytes, position)?, Type::I32 | Type::Native) {
                            return Err("Stack allocation size must be int32/native".into());
                        }
                        Some(Type::Native)
                    }
                    Op::FieldHandle(field) => {
                        if field.token >> 24 != 4 || field.token & 0xffffff == 0 {
                            return Err("Invalid field handle identity".into());
                        }
                        Some(Type::FieldHandle(field))
                    }
                    Op::InitializeArray { array, ref bytes } => {
                        if !matches!(
                            check(array, position)?,
                            Type::Array(Storage::Scalar(_), _) | Type::Null
                        ) || bytes.is_empty()
                            || bytes.len() > 4096
                        {
                            return Err("Invalid primitive array initializer".into());
                        }
                        if matches!(
                            check(array, position)?,
                            Type::Array(
                                Storage::Scalar(Scalar::NativeSigned | Scalar::NativeUnsigned),
                                _
                            )
                        ) {
                            return Err(
                                "Native-width RVA array initialization is unsupported".into()
                            );
                        }
                        None
                    }
                    Op::NewRectangle {
                        element,
                        rows,
                        columns,
                    } => {
                        if check(rows, position)? != Type::I32
                            || check(columns, position)? != Type::I32
                        {
                            return Err("Rectangular dimensions must be int32".into());
                        }
                        Some(Type::Array(element, ArrayKind::Rectangle))
                    }
                    Op::RectangleAddress { array, row, column } => {
                        let Type::Array(element, ArrayKind::Rectangle) = check(array, position)?
                        else {
                            return Err(
                                "Rectangular element address requires a rectangular reference"
                                    .into(),
                            );
                        };
                        if check(row, position)? != Type::I32
                            || check(column, position)? != Type::I32
                        {
                            return Err("Rectangular indices must be int32".into());
                        }
                        Some(element.reference_type())
                    }
                    Op::ArrayDimension { array, dimension } => {
                        if !matches!(check(array, position)?, Type::Array(_, _))
                            || check(dimension, position)? != Type::I32
                        {
                            return Err(
                                "Dimension query requires an array and int32 dimension".into()
                            );
                        }
                        Some(Type::I32)
                    }
                    Op::NewArray {
                        element,
                        length,
                        alignment,
                    } => {
                        if alignment == 0 || alignment > 4096 || !alignment.is_power_of_two() {
                            return Err("Invalid array payload alignment".into());
                        }
                        if !matches!(check(length, position)?, Type::I32 | Type::Native) {
                            return Err("Array length must be int32 or native integer".into());
                        }
                        Some(Type::Array(element, ArrayKind::Vector))
                    }
                    Op::StringLiteral(ref units) => {
                        if units.len() > 8191 {
                            return Err("String literal exceeds ROM0 capacity".into());
                        }
                        Some(Type::String)
                    }
                    Op::StringLength(string) => {
                        if !matches!(check(string, position)?, Type::String | Type::Null) {
                            return Err("String length requires a string reference".into());
                        }
                        Some(Type::I32)
                    }
                    Op::StringChar { string, index } => {
                        if !matches!(check(string, position)?, Type::String | Type::Null)
                            || check(index, position)? != Type::I32
                        {
                            return Err("String indexing requires a string and int32 index".into());
                        }
                        Some(Type::I32)
                    }
                    Op::ArrayLength(array) => {
                        if !matches!(check(array, position)?, Type::Array(_, _) | Type::Null) {
                            return Err("Array length requires an array reference".into());
                        }
                        Some(Type::Native)
                    }
                    Op::ArrayAddress { array, index } => {
                        let Type::Array(element, ArrayKind::Vector) = check(array, position)?
                        else {
                            return Err("Array element address requires an array reference".into());
                        };
                        if !matches!(check(index, position)?, Type::I32 | Type::Native) {
                            return Err("Array index must be int32 or native integer".into());
                        }
                        Some(element.reference_type())
                    }
                    Op::LocalAddress {
                        local: index,
                        storage,
                    } => {
                        if local(index)? != storage.stack_type() || !initialized[index as usize] {
                            return Err(
                                "Local address requires initialized compatible primitive storage"
                                    .into(),
                            );
                        }
                        Some(storage.reference_type())
                    }
                    Op::InitializerAddress(constructor) => {
                        if constructor.token >> 24 != 6 || constructor.token & 0xffffff == 0 {
                            return Err("Invalid initializer state identity".into());
                        }
                        Some(Type::Native)
                    }
                    Op::StaticAddress {
                        field,
                        bytes,
                        alignment,
                    } => {
                        if alignment == 0 || alignment > 4096 || !alignment.is_power_of_two() {
                            return Err("Invalid static storage alignment".into());
                        }
                        if field.token >> 24 != 4
                            || field.token & 0xffffff == 0
                            || (!matches!(bytes, 1 | 2 | 4 | 8)
                                && !matches!(result, Some(Type::StructRef(value)) if value.bytes == bytes))
                        {
                            return Err("Invalid static field address/width".into());
                        }
                        match result {
                            Some(Type::Native) => Some(Type::Native),
                            Some(Type::ByRef(scalar)) if u16::from(scalar.bits()) == bytes * 8 => {
                                result
                            }
                            Some(Type::StructRef(value)) if value.bytes == bytes => result,
                            Some(Type::ReferenceCell(_)) if bytes == 2 => result,
                            _ => {
                                return Err(
                                    "Static address result must match native/reference storage"
                                        .into(),
                                );
                            }
                        }
                    }
                    Op::LoadMemory {
                        address,
                        bits,
                        signed,
                    } => {
                        if !check(address, position)?.addresses(bits) {
                            return Err(
                                "Memory address must be native or a matching primitive reference"
                                    .into(),
                            );
                        }
                        let ty = result.ok_or("Memory load has no result")?;
                        if matches!(ty, Type::Struct(value) if value.references && check(address, position)? != Type::StructRef(value))
                        {
                            return Err(
                                "Reference-bearing struct loads require a typed struct reference"
                                    .into(),
                            );
                        }
                        if matches!(ty, Type::Object(_) | Type::Array(_, _) | Type::String)
                            && !matches!(check(address, position)?, Type::ReferenceCell(cell) if ty == cell.stack_type())
                        {
                            return Err(
                                "Managed-reference load requires a typed reference cell".into()
                            );
                        }
                        if matches!(check(address, position)?, Type::ByRef(scalar) if scalar.stack_type() != ty)
                            || matches!(check(address, position)?, Type::ReferenceCell(cell) if cell.stack_type() != ty)
                            || matches!(check(address, position)?, Type::StructRef(value) if Type::Struct(value) != ty)
                        {
                            return Err(
                                "Managed-reference load differs from storage stack type".into()
                            );
                        }
                        if !matches!(ty, Type::Struct(value) if value.bytes * 8 == bits && !signed)
                            && !matches!(
                                (bits, ty),
                                (8 | 16 | 32, Type::I32)
                                    | (64, Type::I64)
                                    | (
                                        16,
                                        Type::Native
                                            | Type::Array(_, _)
                                            | Type::String
                                            | Type::Object(_)
                                    )
                            )
                        {
                            return Err("Invalid memory load width/result".into());
                        }
                        Some(ty)
                    }
                    Op::StoreMemory {
                        address,
                        value,
                        bits,
                    } => {
                        if !check(address, position)?.addresses(bits) {
                            return Err(
                                "Memory address must be native or a matching primitive reference"
                                    .into(),
                            );
                        }
                        let stored = check(value, position)?;
                        if matches!(stored, Type::Object(_) | Type::Array(_, _) | Type::String)
                            && !matches!(check(address, position)?, Type::ReferenceCell(cell) if cell.stack_type() == stored)
                        {
                            return Err(
                                "Managed-reference stores require a typed reference cell".into()
                            );
                        }
                        if matches!(stored, Type::Struct(value) if value.references && check(address, position)? != Type::StructRef(value))
                        {
                            return Err(
                                "Reference-bearing struct stores require a typed struct reference"
                                    .into(),
                            );
                        }
                        if matches!(check(address, position)?, Type::ByRef(scalar) if scalar.stack_type() != stored)
                            || matches!(check(address, position)?, Type::ReferenceCell(cell) if cell.stack_type() != stored)
                            || matches!(check(address, position)?, Type::StructRef(value) if Type::Struct(value) != stored)
                        {
                            return Err(
                                "Managed-reference store differs from storage stack type".into()
                            );
                        }
                        if !matches!(stored, Type::Struct(value) if value.bytes * 8 == bits)
                            && !matches!(
                                (bits, stored),
                                (8 | 16 | 32, Type::I32)
                                    | (64, Type::I64)
                                    | (
                                        16,
                                        Type::Native
                                            | Type::Array(_, _)
                                            | Type::String
                                            | Type::Object(_)
                                    )
                            )
                        {
                            return Err("Invalid memory store width/value".into());
                        }
                        None
                    }
                    Op::Call {
                        ref signature,
                        ref arguments,
                        ..
                    } => {
                        if signature
                            .parameters
                            .iter()
                            .chain(signature.returns.iter())
                            .any(|ty| matches!(ty, Type::Null | Type::FieldHandle(_)))
                        {
                            return Err(
                                "Metadata-only values cannot use the machine call ABI".into()
                            );
                        }
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
                        if matches!(ty, Type::Struct(_)) {
                            return Err(
                                "Aggregate constants require explicit storage operations".into()
                            );
                        }
                        if matches!(ty, Type::FieldHandle(_)) {
                            return Err("Field handles require metadata identity".into());
                        }
                        if matches!(
                            ty,
                            Type::ByRef(_)
                                | Type::Object(_)
                                | Type::ReferenceCell(_)
                                | Type::StructRef(_)
                                | Type::Array(_, _)
                                | Type::String
                                | Type::Null
                        ) && bits != 0
                        {
                            return Err("Nonzero managed reference constant".into());
                        }
                        if ty.bits() < 64 && bits >= 1u64 << ty.bits() {
                            return Err("Constant exceeds result width".into());
                        }
                        Some(ty)
                    }
                    Op::Binary { op, left, right } => {
                        let left = check(left, position)?;
                        let right = check(right, position)?;
                        if !matches!(left, Type::I32 | Type::I64 | Type::Native)
                            || !matches!(right, Type::I32 | Type::I64 | Type::Native)
                        {
                            return Err(
                                "Managed reference arithmetic requires explicit pointer conversion"
                                    .into(),
                            );
                        }
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
                    Op::Compare { op, left, right } => {
                        if matches!(check(left, position)?, Type::Struct(_)) {
                            return Err("Aggregate comparison is unsupported".into());
                        }
                        if matches!(check(left, position)?, Type::FieldHandle(_)) {
                            return Err("Metadata handle comparison is unsupported".into());
                        }
                        if check(left, position)? != check(right, position)? {
                            return Err("Comparison operand type mismatch".into());
                        }
                        if matches!(check(left, position)?, Type::String | Type::Object(_))
                            && op != Comparison::Equal
                            && !(op == Comparison::GreaterUnsigned
                                && (zeroes.contains(&left) || zeroes.contains(&right)))
                        {
                            return Err(
                                "String references support equality and unsigned null checks"
                                    .into(),
                            );
                        }
                        Some(Type::I32)
                    }
                    Op::Negate(value) | Op::Not(value) => {
                        let ty = check(value, position)?;
                        if !matches!(ty, Type::I32 | Type::I64 | Type::Native) {
                            return Err("Managed reference unary arithmetic".into());
                        }
                        Some(ty)
                    }
                    Op::Convert { value, bits, .. } => {
                        if matches!(check(value, position)?, Type::Struct(_)) {
                            return Err("Aggregate value conversion is unsupported".into());
                        }
                        if matches!(
                            check(value, position)?,
                            Type::Array(_, _)
                                | Type::String
                                | Type::Object(_)
                                | Type::Null
                                | Type::FieldHandle(_)
                        ) {
                            return Err("Managed reference conversion is unsupported".into());
                        }
                        let ty = result.ok_or("Conversion has no result")?;
                        if !matches!(ty, Type::I32 | Type::I64 | Type::Native) {
                            return Err("Conversion cannot fabricate a managed reference".into());
                        }
                        if !matches!(bits, 8 | 16 | 32 | 64) || u16::from(bits) > ty.bits() {
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
                        let ty = local(index)?;
                        if pinned.contains(&index)
                            && (block != 0
                                || initialized[index as usize]
                                || !zeroes.contains(&value))
                        {
                            return Err(
                                "Pinned local must use a pin store outside zero initialization"
                                    .into(),
                            );
                        }
                        if check(value, position)? != ty {
                            return Err("Local store type mismatch".into());
                        }
                        if block == 0 {
                            initialized[index as usize] = true;
                        }
                        None
                    }
                    Op::StorePinned {
                        local: index,
                        value,
                    } => {
                        let ty = local(index)?;
                        if !ty.is_byref() && !matches!(ty, Type::Array(_, _)) {
                            return Err(
                                "Pinned storage requires a managed reference or array".into()
                            );
                        }
                        let input = check(value, position)?;
                        if input != ty && !(ty.is_byref() && input == Type::Native) {
                            return Err("Pinned store type mismatch".into());
                        }
                        if !initialized[index as usize] {
                            return Err("Pinned local lacks entry initialization".into());
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
            if let Control::Switch { selector, .. } = body.terminator.kind {
                if check(selector, position)? != Type::I32 {
                    return Err("Switch selector must be int32".into());
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
    escape::verify(function)?;
    stack_allocations(function)?;
    pins::verify_banks(std::slice::from_ref(function))
}
