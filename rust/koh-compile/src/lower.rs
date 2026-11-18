use crate::{
    cil::{Instruction as Cil, Operand, decode},
    ir::*,
    resolve::Definition,
};
use std::collections::{HashMap, HashSet, VecDeque};
mod checked;
mod int128;
mod spans;

fn primitive_type(ty: &clrmeta::TypeSig) -> Result<Type, String> {
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
        TypeSig::Primitive(ElementType::R4) => Ok(Type::F32),
        TypeSig::Primitive(ElementType::R8) => Ok(Type::F64),
        TypeSig::Primitive(ElementType::String) => Ok(Type::String),
        TypeSig::Primitive(ElementType::IntPtr | ElementType::UIntPtr) | TypeSig::Ptr(_) => {
            Ok(Type::Native)
        }
        TypeSig::ByRef(inner) => Ok(Type::ByRef(scalar(inner)?)),
        TypeSig::SzArray(inner) => Ok(Type::Array(
            Storage::Scalar(scalar(inner)?),
            ArrayKind::Vector,
        )),
        TypeSig::Array { .. } => Ok(Type::Array(
            Storage::Scalar(scalar(rectangle_element(ty)?)?),
            ArrayKind::Rectangle,
        )),
        TypeSig::Pinned(inner) if matches!(**inner, TypeSig::ByRef(_) | TypeSig::SzArray(_)) => {
            primitive_type(inner)
        }
        _ => Err(format!("Type {ty:?} has no supported scalar lowering yet")),
    }
}

pub(crate) fn rectangle_element(ty: &clrmeta::TypeSig) -> Result<&clrmeta::TypeSig, String> {
    let clrmeta::TypeSig::Array {
        element_type,
        rank,
        sizes,
        lo_bounds,
    } = ty
    else {
        return Err("Expected a rectangular array signature".into());
    };
    if *rank != 2 {
        return Err(format!(
            "Rectangular array rank {rank} is unsupported; expected rank2"
        ));
    }
    if !sizes.is_empty() || lo_bounds.len() > 2 || lo_bounds.iter().any(|&bound| bound != 0) {
        return Err("Rectangular arrays require an unsized zero-based rank2 signature".into());
    }
    Ok(element_type)
}

fn scalar(ty: &clrmeta::TypeSig) -> Result<Scalar, String> {
    use clrmeta::{ElementType, TypeSig};
    if matches!(ty, TypeSig::Ptr(_)) {
        return Ok(Scalar::NativeUnsigned);
    }
    let TypeSig::Primitive(ty) = ty else {
        return Err(format!(
            "Managed references require primitive/pointer scalar storage, found {ty:?}"
        ));
    };
    Ok(match ty {
        ElementType::Boolean => Scalar::Bool,
        ElementType::Char => Scalar::Char,
        ElementType::I1 => Scalar::I8,
        ElementType::U1 => Scalar::U8,
        ElementType::I2 => Scalar::I16,
        ElementType::U2 => Scalar::U16,
        ElementType::I4 => Scalar::I32,
        ElementType::U4 => Scalar::U32,
        ElementType::I8 => Scalar::I64,
        ElementType::U8 => Scalar::U64,
        ElementType::R4 => Scalar::F32,
        ElementType::R8 => Scalar::F64,
        ElementType::IntPtr => Scalar::NativeSigned,
        ElementType::UIntPtr => Scalar::NativeUnsigned,
        _ => return Err("Managed reference scalar type is unsupported".into()),
    })
}

pub(crate) fn storage_type(
    resolver: &crate::resolve::Resolver<'_>,
    assembly: usize,
    ty: &clrmeta::TypeSig,
) -> Result<clrmeta::TypeSig, String> {
    use clrmeta::TypeSig;
    if resolver.int128_scalar(assembly, ty)?.is_some() {
        return Ok(ty.clone());
    }
    Ok(match ty {
        TypeSig::ValueType(encoded) => {
            let identity = resolver.signature_type(assembly, *encoded)?;
            let raw = resolver.type_definition(identity)?;
            let metadata = resolver.assembly(raw.assembly)?.metadata();
            let definition = &metadata.type_defs[(raw.token & 0xffffff) as usize - 1];
            if definition.extends.table == Some(clrmeta::TableId::TypeRef)
                && crate::resolve::framework_type(metadata, 0x01000000 | definition.extends.row)?
                    == Some(("System", "Enum"))
            {
                resolver.enum_storage(assembly, *encoded)?
            } else {
                crate::layout::value(resolver, identity)?;
                ty.clone()
            }
        }
        TypeSig::ByRef(inner) => TypeSig::ByRef(Box::new(storage_type(resolver, assembly, inner)?)),
        TypeSig::SzArray(inner) => {
            TypeSig::SzArray(Box::new(storage_type(resolver, assembly, inner)?))
        }
        TypeSig::Array {
            rank,
            sizes,
            lo_bounds,
            ..
        } => TypeSig::Array {
            element_type: Box::new(storage_type(resolver, assembly, rectangle_element(ty)?)?),
            rank: *rank,
            sizes: sizes.clone(),
            lo_bounds: lo_bounds.clone(),
        },
        TypeSig::Pinned(inner) => {
            TypeSig::Pinned(Box::new(storage_type(resolver, assembly, inner)?))
        }
        TypeSig::GenericInst { .. } => {
            if resolver.delegate_signature_type(assembly, ty)?.is_some() {
                return Ok(ty.clone());
            }
            let argument = resolver.bind_signature(assembly, ty, &[], &[])?;
            return storage_type(resolver, argument.assembly, &argument.ty);
        }
        _ => ty.clone(),
    })
}

pub(crate) fn storage_signature(
    resolver: &crate::resolve::Resolver<'_>,
    identity: Definition,
    signature: &clrmeta::MethodSig,
) -> Result<clrmeta::MethodSig, String> {
    let mut result = signature.clone();
    result.params = signature
        .params
        .iter()
        .map(|ty| storage_type(resolver, identity.assembly, ty))
        .collect::<Result<_, _>>()?;
    result.return_type = storage_type(resolver, identity.assembly, &signature.return_type)?;
    if signature.calling_convention.has_this() {
        let owner = owner_type(resolver, identity)?;
        let raw = resolver.type_definition(owner)?;
        let metadata = resolver.assembly(raw.assembly)?.metadata();
        let ty = &metadata.type_defs[(raw.token & 0xffffff) as usize - 1];
        let value = ty.extends.table == Some(clrmeta::TableId::TypeRef)
            && crate::resolve::framework_type(metadata, 0x01000000 | ty.extends.row)?
                == Some(("System", "ValueType"));
        let receiver = resolver.type_argument(owner)?.ty;
        let receiver = if value {
            crate::layout::value(resolver, owner)?;
            clrmeta::TypeSig::ByRef(Box::new(receiver))
        } else {
            crate::layout::class(resolver, owner)?;
            receiver
        };
        result.params.insert(0, receiver);
        result.calling_convention.0 &= !0x20;
    }
    Ok(result)
}

pub(crate) fn owner_type(
    resolver: &crate::resolve::Resolver<'_>,
    identity: Definition,
) -> Result<Definition, String> {
    if let Some(instance) = resolver.instance(identity)? {
        return instance
            .declaring
            .map_or_else(|| owner_type(resolver, instance.method), Ok);
    }
    if let Some(instance) = resolver.field_instance(identity)? {
        return Ok(instance.owner);
    }
    let metadata = resolver.assembly(identity.assembly)?.metadata();
    let rid = identity.token & 0xffffff;
    let owner = match identity.token >> 24 {
        6 => metadata
            .type_defs
            .partition_point(|ty| ty.method_list <= rid),
        4 => metadata
            .type_defs
            .partition_point(|ty| ty.field_list <= rid),
        _ => return Err("Member requires method/field identity".into()),
    };
    if owner == 0 {
        return Err("Member has no declaring type".into());
    }
    Ok(Definition {
        assembly: identity.assembly,
        token: 0x02000000 | owner as u32,
    })
}

fn value_type(layout: &crate::layout::Layout) -> Result<ValueType, String> {
    if layout.contains_references {
        ValueType::with_references(layout.identity, layout.bytes)
    } else {
        ValueType::new(layout.identity, layout.bytes)
    }
}

pub(crate) fn bound_type(
    resolver: &crate::resolve::Resolver<'_>,
    assembly: usize,
    ty: &clrmeta::TypeSig,
) -> Result<Type, String> {
    if let Some(scalar) = resolver.int128_scalar(assembly, ty)? {
        return Ok(scalar.stack_type());
    }
    if let clrmeta::TypeSig::ByRef(inner) = ty {
        if let Some(scalar) = resolver.int128_scalar(assembly, inner)? {
            return Ok(Type::ByRef(scalar));
        }
    }
    if let Some(kind) = crate::delegates::bind(resolver, assembly, ty)? {
        return Ok(Type::Delegate(kind));
    }
    let shape = |encoded| -> Result<ValueType, String> {
        let identity = resolver.signature_type(assembly, encoded)?;
        let layout = crate::layout::value(resolver, identity)?;
        value_type(&layout)
    };
    match ty {
        clrmeta::TypeSig::Primitive(clrmeta::ElementType::Object) => {
            Ok(Type::Object(ClassType::root()))
        }
        clrmeta::TypeSig::Class(encoded) => Ok(Type::Object(class_type(
            resolver,
            resolver.signature_type(assembly, *encoded)?,
        )?)),
        clrmeta::TypeSig::ValueType(encoded) => Ok(Type::Struct(shape(*encoded)?)),
        clrmeta::TypeSig::ByRef(inner) => match **inner {
            clrmeta::TypeSig::ValueType(encoded) => Ok(Type::StructRef(shape(encoded)?)),
            clrmeta::TypeSig::Class(_)
            | clrmeta::TypeSig::GenericInst { .. }
            | clrmeta::TypeSig::Primitive(clrmeta::ElementType::Object) => {
                let cell = match bound_type(resolver, assembly, inner)? {
                    Type::Object(class) => ReferenceCell::Object(class),
                    Type::Delegate(kind) => ReferenceCell::Delegate(kind),
                    _ => return Err("Invalid object reference cell".into()),
                };
                Ok(Type::ReferenceCell(cell))
            }
            clrmeta::TypeSig::Primitive(clrmeta::ElementType::String) => {
                Ok(Type::ReferenceCell(ReferenceCell::String))
            }
            clrmeta::TypeSig::SzArray(ref element) => {
                Ok(Type::ReferenceCell(ReferenceCell::Array(
                    bound_storage(resolver, assembly, element)?,
                    ArrayKind::Vector,
                )))
            }
            clrmeta::TypeSig::Array { .. } => Ok(Type::ReferenceCell(ReferenceCell::Array(
                bound_storage(resolver, assembly, rectangle_element(inner)?)?,
                ArrayKind::Rectangle,
            ))),
            _ => primitive_type(ty),
        },
        clrmeta::TypeSig::Pinned(inner) => bound_type(resolver, assembly, inner),
        clrmeta::TypeSig::SzArray(inner) => Ok(Type::Array(
            bound_storage(resolver, assembly, inner)?,
            ArrayKind::Vector,
        )),
        clrmeta::TypeSig::Array { .. } => Ok(Type::Array(
            bound_storage(resolver, assembly, rectangle_element(ty)?)?,
            ArrayKind::Rectangle,
        )),
        clrmeta::TypeSig::GenericInst { .. } => {
            let argument = resolver.bind_signature(assembly, ty, &[], &[])?;
            bound_type(resolver, argument.assembly, &argument.ty)
        }
        clrmeta::TypeSig::Var(_) | clrmeta::TypeSig::MVar(_) => {
            Err("Unsupported generic type requires instantiated lowering".into())
        }
        _ => primitive_type(ty),
    }
}

fn bound_storage(
    resolver: &crate::resolve::Resolver<'_>,
    assembly: usize,
    signature: &clrmeta::TypeSig,
) -> Result<Storage, String> {
    if let Some(scalar) = resolver.int128_scalar(assembly, signature)? {
        return Ok(Storage::Scalar(scalar));
    }
    let signature = storage_type(resolver, assembly, signature)?;
    match bound_type(resolver, assembly, &signature)? {
        Type::Object(class) => Ok(Storage::Object(class)),
        Type::Delegate(kind) => Ok(Storage::Delegate(kind)),
        Type::String => Ok(Storage::String),
        Type::Struct(value) => Ok(Storage::Struct(value)),
        _ => Ok(Storage::Scalar(scalar(&signature)?)),
    }
}

pub(crate) fn class_type(
    resolver: &crate::resolve::Resolver<'_>,
    identity: Definition,
) -> Result<ClassType, String> {
    let layout = crate::layout::class(resolver, identity)?;
    if layout.interface {
        return ClassType::interface(layout.identity);
    }
    ClassType::new(layout.identity, layout.base, layout.bytes)
}

fn parent_class(
    resolver: &crate::resolve::Resolver<'_>,
    class: ClassType,
) -> Result<ClassType, String> {
    match class.base() {
        Some(id) => class_type(resolver, id),
        None => Ok(ClassType::root()),
    }
}

fn common_class(
    resolver: &crate::resolve::Resolver<'_>,
    left: ClassType,
    mut right: ClassType,
) -> Result<ClassType, String> {
    let mut ancestors = vec![left];
    let mut current = left;
    while current.identity().is_some() {
        if ancestors.len() > 64 {
            return Err("Class ancestry exceeds64".into());
        }
        current = parent_class(resolver, current)?;
        ancestors.push(current);
    }
    for _ in 0..=64 {
        if ancestors.contains(&right) {
            return Ok(right);
        }
        right = parent_class(resolver, right)?;
    }
    Err("Inconsistent class ancestry".into())
}

pub(crate) fn field_storage(
    resolver: &crate::resolve::Resolver<'_>,
    assembly: usize,
    signature: &clrmeta::TypeSig,
) -> Result<FieldStorage, String> {
    match bound_type(
        resolver,
        assembly,
        &storage_type(resolver, assembly, signature)?,
    )? {
        Type::Array(element, kind) => Ok(FieldStorage::Array(element, kind)),
        _ => Ok(FieldStorage::Value(bound_storage(
            resolver, assembly, signature,
        )?)),
    }
}

pub(crate) fn bound_signature(
    resolver: &crate::resolve::Resolver<'_>,
    assembly: usize,
    signature: &clrmeta::MethodSig,
) -> Result<Signature, String> {
    if signature.calling_convention.has_this() || signature.generic_param_count != 0 {
        return Err("Instance/generic methods require instantiated lowering".into());
    }
    if signature.calling_convention.0 & 0x0f != 0 || signature.params.len() > 1024 {
        return Err("Unsupported calling convention or parameter count".into());
    }
    let parameters = signature
        .params
        .iter()
        .map(|ty| bound_type(resolver, assembly, ty))
        .collect::<Result<_, _>>()?;
    let returns =
        if signature.return_type == clrmeta::TypeSig::Primitive(clrmeta::ElementType::Void) {
            None
        } else {
            Some(bound_type(resolver, assembly, &signature.return_type)?)
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
    let emitted_identity = identity;
    let instance = resolver.instance(identity)?;
    let identity = instance
        .as_ref()
        .map_or(identity, |instance| instance.method);
    let arguments = instance
        .as_ref()
        .map_or(&[][..], |instance| instance.arguments.as_slice());
    let declaring = instance.as_ref().and_then(|instance| instance.declaring);
    let type_arguments = declaring
        .map(|owner| resolver.type_instance(owner))
        .transpose()?
        .flatten()
        .map_or_else(Vec::new, |instance| instance.arguments);
    let assembly = resolver.assembly(identity.assembly)?;
    let result = || -> Result<Function, String> {
        crate::contracts::method(resolver, identity)?;
        let crate::generics::MethodTypes {
            parameters: signature_parameters,
            returns: signature_return,
            signature: typed,
        } = crate::generics::method_types(
            resolver,
            emitted_identity,
            &assembly.method_signature(identity.token)?,
            arguments,
        )?;
        let local_types = assembly
            .local_types(identity.token)?
            .iter()
            .map(|ty| {
                crate::generics::storage_in(
                    resolver,
                    identity.assembly,
                    ty,
                    arguments,
                    &type_arguments,
                )
            })
            .collect::<Result<Vec<_>, _>>()?;
        let locals = local_types
            .iter()
            .map(|ty| bound_type(resolver, ty.assembly, &ty.ty))
            .collect::<Result<_, _>>()?;
        let pinned: Vec<_> = local_types
            .iter()
            .enumerate()
            .filter_map(|(index, ty)| {
                matches!(ty.ty, clrmeta::TypeSig::Pinned(_))
                    .then_some(index + signature_parameters.len())
            })
            .collect();
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
            &pinned,
            arguments,
            declaring,
        )?;
        let mut prefix = Vec::new();
        for constructor in [
            resolver.module_initializer(identity.assembly)?,
            resolver.method_initializer(emitted_identity)?,
        ]
        .into_iter()
        .flatten()
        {
            if constructor != emitted_identity {
                prefix.push(Instruction {
                    result: None,
                    op: crate::initialize::guard(constructor),
                    il_offset: 0,
                });
            }
        }
        function.blocks[0].instructions.splice(0..0, prefix);
        let mut slots: Vec<_> = signature_parameters
            .iter()
            .chain(&local_types)
            .map(|argument| narrow_type(&argument.ty))
            .collect();
        slots.resize(function.locals.len(), None);
        normalize_storage(&mut function, &slots, narrow_type(&signature_return.ty))?;
        function.identity = emitted_identity;
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
            if let Op::LoadLocal(index) = instruction.op {
                if let Some(shape) = slots[index as usize] {
                    let original = instruction.result.ok_or("Local load has no result")?;
                    let mut convert = conversion(original.0, shape, instruction.il_offset)?;
                    let loaded = convert.result.ok_or("Storage conversion has no result")?;
                    instruction.result = Some(loaded);
                    convert.result = Some(original);
                    if let Op::Convert { ref mut value, .. } = convert.op {
                        *value = loaded.0;
                    }
                    instructions.push(instruction);
                    instructions.push(convert);
                    continue;
                }
            }
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

struct Builder<'r, 'a> {
    resolver: Option<&'r crate::resolve::Resolver<'a>>,
    method_arguments: Vec<crate::generics::Argument>,
    type_arguments: Vec<crate::generics::Argument>,
    declaring: Option<Definition>,
    next: u32,
    data_bytes: usize,
    edge_arguments: usize,
    pinned: HashSet<usize>,
    constructions: HashMap<u32, (u32, ValueType)>,
    delegate_sequences: HashMap<u32, bool>,
    constrained_calls: HashMap<u32, u32>,
    instructions: Vec<Instruction>,
    stack: Vec<(u32, Type)>,
    offset: u32,
}

impl Builder<'_, '_> {
    fn coerce(&mut self, value: (u32, Type), ty: Type) -> Result<(u32, Type), String> {
        if value.1 == Type::Null
            && matches!(
                ty,
                Type::Delegate(_) | Type::Array(_, _) | Type::String | Type::Object(_)
            )
        {
            self.value(ty, Op::Constant(0))
        } else if value.1 == ty {
            Ok(value)
        } else if let (Type::Object(mut source), Type::Object(target)) = (value.1, ty) {
            let resolver = self.resolver.ok_or("Object coercion requires metadata")?;
            if target.is_interface() {
                let assignable = source
                    .identity()
                    .map(|id| crate::layout::implements(resolver, id, target.identity().unwrap()))
                    .transpose()?
                    .unwrap_or(false);
                if !assignable {
                    if crate::generics::variance::parameters(resolver, target.identity().unwrap())?
                        .iter()
                        .any(|&flag| flag != 0)
                    {
                        return Err(
                            "Variant interface conversion requires variance-aware binding".into(),
                        );
                    }
                    return Err("Object reference does not implement requested interface".into());
                }
                return self.value(
                    ty,
                    Op::CastClass {
                        object: value.0,
                        class: target,
                    },
                );
            }
            let mut value = value;
            for _ in 0..64 {
                if source.identity().is_none() {
                    break;
                }
                let parent = parent_class(resolver, source)?;
                value = self.value(
                    Type::Object(parent),
                    Op::Upcast {
                        value: value.0,
                        class: parent,
                    },
                )?;
                if parent == target {
                    return Ok(value);
                }
                source = parent;
            }
            Err("Object reference is not assignable to requested class".into())
        } else {
            Err(format!(
                "CIL operand type mismatch: {:?}, expected {ty:?}",
                value.1
            ))
        }
    }
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

    fn float_call(&mut self, key: &str, left: u32, right: u32) -> Result<(u32, Type), String> {
        let resolver = self
            .resolver
            .ok_or("Floating operation requires runtime metadata")?;
        let target = crate::contracts::float_binary(resolver, key)?;
        let raw = if key.starts_with("f64.") {
            Type::I64
        } else {
            Type::I32
        };
        let returns = if key.ends_with(".cmp") {
            Type::I32
        } else {
            raw
        };
        let left = self.value(raw, Op::Reinterpret(left))?;
        let right = self.value(raw, Op::Reinterpret(right))?;
        self.value(
            returns,
            Op::Call {
                target,
                signature: Signature {
                    parameters: vec![raw, raw],
                    returns: Some(returns),
                },
                arguments: vec![left.0, right.0],
            },
        )
    }

    fn float_conversion(
        &mut self,
        key: &str,
        mut value: (u32, Type),
    ) -> Result<(u32, Type), String> {
        if matches!(key, "i64.toF64" | "u64.toF64") && matches!(value.1, Type::I32 | Type::Native) {
            value = self.value(
                Type::I64,
                Op::Convert {
                    value: value.0,
                    bits: value.1.bits() as u8,
                    signed: key == "i64.toF64",
                },
            )?;
        }
        value = match value.1 {
            Type::F32 => self.value(Type::I32, Op::Reinterpret(value.0))?,
            Type::F64 => self.value(Type::I64, Op::Reinterpret(value.0))?,
            Type::Native => self.value(
                Type::I32,
                Op::Convert {
                    value: value.0,
                    bits: 16,
                    signed: key == "i32.toF32",
                },
            )?,
            Type::I32 | Type::I64 => value,
            _ => return Err("Unsupported floating conversion operand".into()),
        };
        let resolver = self
            .resolver
            .ok_or("Floating conversion requires runtime metadata")?;
        let target = crate::contracts::float_conversion(resolver, key)?;
        let returns = if matches!(
            key,
            "f32.toI64"
                | "f32.toU64"
                | "f32.toF64"
                | "i64.toF64"
                | "u64.toF64"
                | "f64.toI64"
                | "f64.toU64"
        ) {
            Type::I64
        } else {
            Type::I32
        };
        self.value(
            returns,
            Op::Call {
                target,
                signature: Signature {
                    parameters: vec![value.1],
                    returns: Some(returns),
                },
                arguments: vec![value.0],
            },
        )
    }

    fn binary(&mut self, op: Binary) -> Result<(), String> {
        let mut right = self.pop()?;
        let mut left = self.pop()?;
        if matches!(left.1, Type::F32 | Type::F64) || matches!(right.1, Type::F32 | Type::F64) {
            if left.1 != right.1 {
                return Err("Unsupported mixed floating binary operand types".into());
            }
            let key = match (left.1, op) {
                (Type::F32, Binary::Add) => "f32.add",
                (Type::F32, Binary::Subtract) => "f32.sub",
                (Type::F32, Binary::Multiply) => "f32.mul",
                (Type::F32, Binary::DivideSigned) => "f32.div",
                (Type::F32, Binary::RemainderSigned) => "f32.rem",
                (Type::F64, Binary::Add) => "f64.add",
                (Type::F64, Binary::Subtract) => "f64.sub",
                (Type::F64, Binary::Multiply) => "f64.mul",
                (Type::F64, Binary::DivideSigned) => "f64.div",
                (Type::F64, Binary::RemainderSigned) => "f64.rem",
                _ => {
                    return Err(format!(
                        "Unsupported floating binary operation {op:?} on {:?}",
                        left.1
                    ));
                }
            };
            let bits = self.float_call(key, left.0, right.0)?;
            let value = self.value(left.1, Op::Reinterpret(bits.0))?;
            self.stack.push(value);
            return Ok(());
        }
        if !matches!(left.1, Type::I32 | Type::I64 | Type::Native)
            || !matches!(right.1, Type::I32 | Type::I64 | Type::Native)
        {
            return Err("Managed reference arithmetic requires pointer conversion".into());
        }
        let shift = matches!(
            op,
            Binary::ShiftLeft | Binary::ShiftRightSigned | Binary::ShiftRightUnsigned
        );
        if !shift
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
        if shift && right.1 == Type::Native {
            right = self.value(
                Type::I32,
                Op::Convert {
                    value: right.0,
                    bits: 16,
                    signed: false,
                },
            )?;
        }
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
        let mut right = self.pop()?;
        let mut left = self.pop()?;
        if matches!(left.1, Type::F32 | Type::F64) || matches!(right.1, Type::F32 | Type::F64) {
            if left.1 != right.1 {
                return Err("Unsupported mixed floating comparison operand types".into());
            }
            let key = if left.1 == Type::F64 {
                "f64.cmp"
            } else {
                "f32.cmp"
            };
            let code = self.float_call(key, left.0, right.0)?;
            let expected = match op {
                Comparison::Equal => 0,
                Comparison::LessSigned | Comparison::LessUnsigned => 1,
                Comparison::GreaterSigned | Comparison::GreaterUnsigned => 2,
            };
            let expected = self.value(Type::I32, Op::Constant(expected))?;
            let ordered = self.value(
                Type::I32,
                Op::Compare {
                    op: Comparison::Equal,
                    left: code.0,
                    right: expected.0,
                },
            )?;
            if matches!(op, Comparison::LessUnsigned | Comparison::GreaterUnsigned) {
                let nan = self.value(Type::I32, Op::Constant(3))?;
                let unordered = self.value(
                    Type::I32,
                    Op::Compare {
                        op: Comparison::Equal,
                        left: code.0,
                        right: nan.0,
                    },
                )?;
                return self.value(
                    Type::I32,
                    Op::Binary {
                        op: Binary::Or,
                        left: ordered.0,
                        right: unordered.0,
                    },
                );
            }
            return Ok(ordered);
        }
        if let (Type::Object(a), Type::Object(b)) = (left.1, right.1) {
            let class = common_class(
                self.resolver.ok_or("Object comparison requires metadata")?,
                a,
                b,
            )?;
            left = self.coerce(left, Type::Object(class))?;
            right = self.coerce(right, Type::Object(class))?;
        }
        if left.1 == Type::Null {
            left = self.coerce(left, right.1)?;
        }
        if right.1 == Type::Null {
            right = self.coerce(right, left.1)?;
        }
        if matches!(
            (left.1, right.1),
            (Type::Native, Type::I32) | (Type::I32, Type::Native)
        ) {
            let value = if left.1 == Type::Native {
                &mut left
            } else {
                &mut right
            };
            *value = self.value(
                Type::I32,
                Op::Convert {
                    value: value.0,
                    bits: 16,
                    signed: matches!(op, Comparison::LessSigned | Comparison::GreaterSigned),
                },
            )?;
        }
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
    lower_with(method, None, &[], &[], None)
}

fn lower_with(
    method: Method<'_>,
    resolver: Option<&crate::resolve::Resolver<'_>>,
    pinned: &[usize],
    method_arguments: &[crate::generics::Argument],
    declaring: Option<Definition>,
) -> Result<Function, String> {
    lower_inner(&method, resolver, pinned, method_arguments, declaring).map_err(|error| {
        format!(
            "Assembly {} method 0x{:08x}: {error}",
            method.identity.assembly, method.identity.token
        )
    })
}

fn lower_inner(
    method: &Method<'_>,
    resolver: Option<&crate::resolve::Resolver<'_>>,
    pinned: &[usize],
    method_arguments: &[crate::generics::Argument],
    declaring: Option<Definition>,
) -> Result<Function, String> {
    let cil = decode(method.il)?;
    if cil.len() >= 16_384
        || cil
            .iter()
            .map(|i| match &i.operand {
                Operand::Targets(targets) => targets.len(),
                _ => 0,
            })
            .sum::<usize>()
            > 262_144
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
        resolver,
        method_arguments: method_arguments.to_vec(),
        type_arguments: if let (Some(resolver), Some(owner)) = (resolver, declaring) {
            resolver
                .type_instance(owner)?
                .ok_or("Missing declaring type binding")?
                .arguments
        } else {
            Vec::new()
        },
        declaring,
        next: method.parameters.len() as u32,
        data_bytes: 0,
        edge_arguments: 0,
        pinned: pinned.iter().copied().collect(),
        constructions: HashMap::new(),
        delegate_sequences: crate::delegates::construction_sequences(&cil),
        constrained_calls: crate::constrained::prefixes(&cil)?,
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
        let value = builder
            .value(
                ty,
                if matches!(ty, Type::Struct(_)) {
                    Op::ZeroStruct
                } else {
                    Op::Constant(0)
                },
            )?
            .0;
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
        builder.edge_arguments += kind.edges().map(|e| e.arguments.len()).sum::<usize>();
        if builder.edge_arguments > 1_048_576 {
            return Err(format!(
                "IL_{:04x}: Evaluation stack edge arguments exceed limit",
                instruction.offset
            ));
        }
        if builder.stack.len() > usize::from(method.max_stack) {
            return Err(format!(
                "IL_{:04x}: Evaluation stack exceeds declared maxstack",
                instruction.offset
            ));
        }
        let shape: Vec<_> = builder.stack.iter().map(|v| v.1).collect();
        for edge in kind.edges() {
            if let Some(expected) = &mut incoming[edge.block] {
                let mut changed = false;
                if expected.len() != shape.len() {
                    return Err("Incompatible evaluation stack heights".into());
                }
                for (old, &new) in expected.iter_mut().zip(&shape) {
                    if let (Type::Object(a), Type::Object(b)) = (*old, new) {
                        let merged = Type::Object(common_class(
                            resolver.ok_or("Object merge requires metadata")?,
                            a,
                            b,
                        )?);
                        changed |= *old != merged;
                        *old = merged;
                    } else if *old == Type::Null
                        && matches!(
                            new,
                            Type::Delegate(_) | Type::Array(_, _) | Type::String | Type::Object(_)
                        )
                    {
                        *old = new;
                        changed = true;
                    } else if *old != new
                        && !(new == Type::Null
                            && matches!(
                                old,
                                Type::Delegate(_)
                                    | Type::Array(_, _)
                                    | Type::String
                                    | Type::Object(_)
                            ))
                    {
                        return Err(format!(
                            "IL_{:04x}: Incompatible evaluation stacks at merge IL_{:04x}",
                            instruction.offset,
                            cil[edge.block - 1].offset
                        ));
                    }
                }
                if changed {
                    queue.push_back(edge.block);
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
    let types: HashMap<_, _> = blocks
        .iter()
        .flatten()
        .flat_map(|b| {
            b.parameters
                .iter()
                .copied()
                .chain(b.instructions.iter().filter_map(|i| i.result))
        })
        .collect();
    for body in blocks.iter_mut().flatten() {
        for edge in body.terminator.kind.edges_mut() {
            for (argument, &ty) in edge
                .arguments
                .iter_mut()
                .zip(incoming[edge.block].as_deref().unwrap_or(&[]))
            {
                if types[argument] != ty {
                    builder.offset = body.terminator.il_offset;
                    let converted = builder.coerce((*argument, types[argument]), ty)?;
                    body.instructions.append(&mut builder.instructions);
                    *argument = converted.0;
                }
            }
        }
    }
    let mut remap = vec![0; blocks.len()];
    let mut next = 0;
    for (i, block) in blocks.iter().enumerate() {
        if block.is_some() {
            remap[i] = next;
            next += 1;
        }
    }
    let mut blocks: Vec<Block> = blocks
        .into_iter()
        .flatten()
        .map(|mut body| {
            for edge in body.terminator.kind.edges_mut() {
                edge.block = remap[edge.block];
            }
            body
        })
        .collect();
    let mut constructions: Vec<_> = std::mem::take(&mut builder.constructions)
        .into_values()
        .collect();
    constructions.sort_unstable_by_key(|(index, _)| *index);
    builder.offset = 0;
    for &(local, shape) in &constructions {
        let zero = builder.value(Type::Struct(shape), Op::ZeroStruct)?;
        builder.instructions.push(Instruction {
            result: None,
            op: Op::StoreLocal {
                local,
                value: zero.0,
            },
            il_offset: 0,
        });
    }
    blocks[0].instructions.append(&mut builder.instructions);
    locals.extend(
        constructions
            .into_iter()
            .map(|(_, shape)| Type::Struct(shape)),
    );
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

fn construct(
    b: &mut Builder,
    ty: Type,
    target: Definition,
    signature: Signature,
    mut arguments: Vec<u32>,
    locals: usize,
) -> Result<(), String> {
    if let Type::Object(class) = ty {
        let object = b.value(Type::Object(class), Op::NewObject(class))?;
        arguments.insert(0, object.0);
        b.instructions.push(Instruction {
            result: None,
            op: Op::Call {
                target,
                signature,
                arguments,
            },
            il_offset: b.offset,
        });
        b.stack.push(object);
        return Ok(());
    }
    if let Type::Struct(shape) = ty {
        let local = if let Some(&(local, old)) = b.constructions.get(&b.offset) {
            if old != shape {
                return Err("Constructor storage type changed during stack merging".into());
            }
            local
        } else {
            let local = locals + b.constructions.len();
            if local >= 1024 {
                return Err("Constructor storage exceeds method local limit".into());
            }
            b.constructions.insert(b.offset, (local as u32, shape));
            local as u32
        };
        let zero = b.value(Type::Struct(shape), Op::ZeroStruct)?;
        b.instructions.push(Instruction {
            result: None,
            op: Op::StoreLocal {
                local,
                value: zero.0,
            },
            il_offset: b.offset,
        });
        let address = b.value(
            Type::StructRef(shape),
            Op::LocalAddress {
                local,
                storage: FieldStorage::Value(Storage::Struct(shape)),
            },
        )?;
        arguments.insert(0, address.0);
        b.instructions.push(Instruction {
            result: None,
            op: Op::Call {
                target,
                signature,
                arguments,
            },
            il_offset: b.offset,
        });
        let value = b.value(
            Type::Struct(shape),
            Op::LoadMemory {
                address: address.0,
                bits: shape.bytes() * 8,
                signed: false,
            },
        )?;
        b.stack.push(value);
        return Ok(());
    }
    Err("Construction requires a value or reference type".into())
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
        let pinned = b.pinned.contains(&index);
        let value = if pinned && ty.is_byref() && value.1 == Type::Native {
            value
        } else {
            b.coerce(value, ty)?
        };
        b.instructions.push(Instruction {
            result: None,
            op: if pinned {
                Op::StorePinned {
                    local: index as u32,
                    value: value.0,
                }
            } else {
                Op::StoreLocal {
                    local: index as u32,
                    value: value.0,
                }
            },
            il_offset: b.offset,
        });
        Ok(())
    };
    match i.opcode {
        0xfe06 | 0xfe07 => {
            let Operand::Token(token) = i.operand else {
                return Err("Missing method-pointer token".into());
            };
            let resolver = resolver.ok_or("Method pointer requires metadata resolution")?;
            let resolved = resolver.resolve_method_in(
                method.identity.assembly,
                token,
                &b.method_arguments,
                &b.type_arguments,
            )?;
            let instantiated = resolver.instantiate_in(
                &resolved,
                &b.method_arguments,
                &b.type_arguments,
                b.declaring,
            )?;
            let image = resolver.assembly(resolved.definition.assembly)?;
            let flags = image.method(resolved.definition.token)?.flags;
            let virtual_pointer = i.opcode == 0xfe07;
            if virtual_pointer && flags & 0x10 != 0 {
                return Err("ldvirtftn requires an instance method".into());
            }
            if !virtual_pointer && flags & 0x400 != 0 {
                return Err("ldftn cannot bind an abstract method".into());
            }
            if virtual_pointer {
                let receiver = b.pop()?;
                let owner = owner_type(resolver, instantiated)?;
                let receiver = b.coerce(receiver, Type::Object(class_type(resolver, owner)?))?;
                b.instructions.push(Instruction {
                    result: None,
                    op: Op::CheckObject(receiver.0),
                    il_offset: b.offset,
                });
            }
            let target = if virtual_pointer && flags & 0x40 != 0 {
                crate::dispatch::identity(instantiated)
            } else {
                instantiated
            };
            let value = b.value(Type::MethodHandle(target), Op::MethodHandle(target))?;
            b.stack.push(value);
        }
        0x72 => {
            let Operand::Token(token) = i.operand else {
                return Err("Missing ldstr token".into());
            };
            let resolver = resolver.ok_or("ldstr requires metadata resolution")?;
            let units = resolver
                .assembly(method.identity.assembly)?
                .user_string(token)?;
            if units.len() > 8191 {
                return Err("String literal exceeds ROM0 capacity".into());
            }
            b.data_bytes += units.len() * 2 + 2;
            if b.data_bytes > 65536 {
                return Err("Method literal data exceeds 64 KiB budget".into());
            }
            let value = b.value(Type::String, Op::StringLiteral(units))?;
            b.stack.push(value);
        }
        0x45 => {
            let Operand::Targets(targets) = &i.operand else {
                return Err("Missing switch targets".into());
            };
            if targets.len() > 16_384 {
                return Err("Switch case count exceeds limit".into());
            }
            let selector = b.pop()?;
            if selector.1 != Type::I32 {
                return Err("Switch selector must be int32".into());
            }
            if (targets.len() + 1)
                .checked_mul(b.stack.len())
                .and_then(|count| b.edge_arguments.checked_add(count))
                .is_none_or(|count| count > 1_048_576)
            {
                return Err("Switch evaluation stack edge arguments exceed limit".into());
            }
            let cases = targets
                .iter()
                .map(|offset| {
                    indices
                        .get(offset)
                        .copied()
                        .map(|block| b.edge(block))
                        .ok_or_else(|| "Invalid switch target".to_string())
                })
                .collect::<Result<_, _>>()?;
            return Ok(Control::Switch {
                selector: selector.0,
                cases,
                default: b.edge(next()?),
            });
        }
        0x70 | 0x71 | 0x81 | 0xfe15 | 0xfe1c => {
            let Operand::Token(token) = i.operand else {
                return Err("Missing scalar type token".into());
            };
            let resolver = resolver.ok_or("Typed scalar operations require metadata resolution")?;
            let storage = {
                let operand = crate::generics::storage_in(
                    resolver,
                    method.identity.assembly,
                    &resolver.type_signature(method.identity.assembly, token)?,
                    &b.method_arguments,
                    &b.type_arguments,
                )?;
                field_storage(resolver, operand.assembly, &operand.ty)?
            };
            let ty = storage.stack_type();
            if i.opcode == 0xfe1c {
                let value = b.value(Type::I32, Op::Constant(u64::from(storage.bytes())))?;
                b.stack.push(value);
            } else {
                let stored = if i.opcode == 0x70 {
                    let source = b.pop()?;
                    if !source.1.addresses(storage.bytes() * 8)
                        || matches!(source.1, Type::ByRef(scalar) if scalar.stack_type() != ty)
                        || matches!(source.1, Type::StructRef(value) if Type::Struct(value) != ty)
                    {
                        return Err("Typed copy source differs from storage type".into());
                    }
                    Some(b.value(
                        ty,
                        Op::LoadMemory {
                            address: source.0,
                            bits: storage.bytes() * 8,
                            signed: matches!(storage, FieldStorage::Value(Storage::Scalar(scalar)) if scalar.signed()),
                        },
                    )?)
                } else if i.opcode == 0x81 {
                    Some(b.pop()?)
                } else if i.opcode == 0xfe15 {
                    Some(b.value(
                        ty,
                        if matches!(ty, Type::Struct(_)) {
                            Op::ZeroStruct
                        } else {
                            Op::Constant(0)
                        },
                    )?)
                } else {
                    None
                };
                let address = b.pop()?;
                if !address.1.addresses(storage.bytes() * 8)
                    || matches!(address.1, Type::ByRef(scalar) if scalar.stack_type() != ty)
                    || matches!(address.1, Type::StructRef(value) if Type::Struct(value) != ty)
                {
                    return Err("Typed scalar address differs from its storage type".into());
                }
                if let Some(value) = stored {
                    if value.1 != ty {
                        return Err("Typed scalar store value type mismatch".into());
                    }
                    b.instructions.push(Instruction {
                        result: None,
                        op: Op::StoreMemory {
                            address: address.0,
                            value: value.0,
                            bits: storage.bytes() * 8,
                        },
                        il_offset: b.offset,
                    });
                } else {
                    let signed = matches!(storage, FieldStorage::Value(Storage::Scalar(scalar)) if scalar.signed());
                    let value = b.value(
                        ty,
                        Op::LoadMemory {
                            address: address.0,
                            bits: storage.bytes() * 8,
                            signed,
                        },
                    )?;
                    b.stack.push(value);
                }
            }
        }
        0xfe0f => {
            let size = b.pop()?;
            if !b.stack.is_empty() {
                return Err("localloc requires an otherwise empty evaluation stack".into());
            }
            if !matches!(size.1, Type::I32 | Type::Native) {
                return Err("localloc requires an int32/native byte count".into());
            }
            let value = b.value(Type::Native, Op::StackAllocate { bytes: size.0 })?;
            b.stack.push(value);
        }
        0xd0 => {
            let Operand::Token(token) = i.operand else {
                return Err("Missing ldtoken field identity".into());
            };
            let resolver = resolver.ok_or("ldtoken requires metadata resolution")?;
            let field = resolver.resolve_field(method.identity.assembly, token)?;
            if !field.type_arguments.is_empty() {
                return Err("Generic RVA field handles are unsupported".into());
            }
            resolver
                .assembly(field.definition.assembly)?
                .field_data(field.definition.token)?;
            let value = b.value(
                Type::FieldHandle(field.definition),
                Op::FieldHandle(field.definition),
            )?;
            b.stack.push(value);
        }
        0x74 | 0x75 => {
            let Operand::Token(token) = i.operand else {
                return Err("Missing castclass type".into());
            };
            let resolver = resolver.ok_or("castclass requires metadata resolution")?;
            let operand = crate::generics::storage_in(
                resolver,
                method.identity.assembly,
                &resolver.type_signature(method.identity.assembly, token)?,
                &b.method_arguments,
                &b.type_arguments,
            )?;
            let Type::Object(class) = bound_type(resolver, operand.assembly, &operand.ty)? else {
                return Err("castclass requires a supported reference class".into());
            };
            if let Some(identity) = class.identity() {
                if class.is_interface()
                    && crate::generics::variance::parameters(resolver, identity)?
                        .iter()
                        .any(|&flag| flag != 0)
                {
                    return Err(
                        "Variant interface casts require variance-aware runtime tests".into(),
                    );
                }
            }
            let object = b.pop()?;
            let result = b.value(
                Type::Object(class),
                if i.opcode == 0x75 {
                    Op::TryCastClass {
                        object: object.0,
                        class,
                    }
                } else {
                    Op::CastClass {
                        object: object.0,
                        class,
                    }
                },
            )?;
            b.stack.push(result);
        }
        0x8d => {
            let Operand::Token(token) = i.operand else {
                return Err("Missing array element type".into());
            };
            let resolver = resolver.ok_or("Array element requires metadata resolution")?;
            let element = {
                let operand = crate::generics::storage_in(
                    resolver,
                    method.identity.assembly,
                    &resolver.type_signature(method.identity.assembly, token)?,
                    &b.method_arguments,
                    &b.type_arguments,
                )?;
                bound_storage(resolver, operand.assembly, &operand.ty)?
            };
            let length = b.pop()?;
            if !matches!(length.1, Type::I32 | Type::Native) {
                return Err("Invalid array length type".into());
            }
            let value = b.value(
                Type::Array(element, ArrayKind::Vector),
                Op::NewArray {
                    element,
                    length: length.0,
                    alignment: 1,
                },
            )?;
            b.stack.push(value);
        }
        0x8e => {
            let array = b.pop()?;
            if !matches!(array.1, Type::Array(_, _) | Type::Null) {
                return Err("ldlen requires an array".into());
            }
            let value = b.value(Type::Native, Op::ArrayLength(array.0))?;
            b.stack.push(value);
        }
        0x8f..=0xa4 => {
            let reference = matches!(i.opcode, 0x9a | 0xa2);
            let store = matches!(i.opcode, 0x9b..=0xa2 | 0xa4);
            let stored = if store { Some(b.pop()?) } else { None };
            let index = b.pop()?;
            if !matches!(index.1, Type::I32 | Type::Native) {
                return Err("Invalid array index type".into());
            }
            let raw = b.pop()?;
            let explicit = if matches!(i.opcode, 0x8f | 0xa3 | 0xa4) {
                let Operand::Token(token) = i.operand else {
                    return Err("Missing array element type".into());
                };
                let resolver = resolver.ok_or("Array element requires metadata resolution")?;
                Some({
                    let operand = crate::generics::storage_in(
                        resolver,
                        method.identity.assembly,
                        &resolver.type_signature(method.identity.assembly, token)?,
                        &b.method_arguments,
                        &b.type_arguments,
                    )?;
                    bound_storage(resolver, operand.assembly, &operand.ty)?
                })
            } else {
                None
            };
            let default = match i.opcode {
                0x90 | 0x9c => Scalar::I8,
                0x91 => Scalar::U8,
                0x92 | 0x9d => Scalar::I16,
                0x93 => Scalar::U16,
                0x94 | 0x9e => Scalar::I32,
                0x95 => Scalar::U32,
                0x96 | 0x9f => Scalar::I64,
                0x98 | 0xa0 => Scalar::F32,
                0x99 | 0xa1 => Scalar::F64,
                _ => Scalar::NativeSigned,
            };
            let array = if raw.1 == Type::Null {
                b.coerce(
                    raw,
                    Type::Array(
                        explicit.unwrap_or(if reference {
                            Storage::Object(ClassType::root())
                        } else {
                            Storage::Scalar(default)
                        }),
                        ArrayKind::Vector,
                    ),
                )?
            } else {
                raw
            };
            let Type::Array(element, ArrayKind::Vector) = array.1 else {
                return Err("Array operation requires an array reference".into());
            };
            if explicit.is_some_and(|ty| ty != element)
                || (explicit.is_none()
                    && if reference {
                        !matches!(
                            element,
                            Storage::Delegate(_) | Storage::Object(_) | Storage::String
                        )
                    } else {
                        element.bytes() * 8 != u16::from(default.bits())
                            || element.stack_type() != default.stack_type()
                    })
            {
                return Err("Array opcode differs from element storage type".into());
            }
            let address = b.value(
                element.reference_type(),
                Op::ArrayAddress {
                    array: array.0,
                    index: index.0,
                },
            )?;
            if i.opcode == 0x8f {
                b.stack.push(address);
            } else if let Some(value) = stored {
                let value = b.coerce(value, element.stack_type())?;
                b.instructions.push(Instruction {
                    result: None,
                    op: Op::StoreMemory {
                        address: address.0,
                        value: value.0,
                        bits: element.bytes() * 8,
                    },
                    il_offset: b.offset,
                });
            } else {
                let signed = if explicit.is_some() {
                    matches!(element, Storage::Scalar(scalar) if scalar.signed())
                } else {
                    matches!(i.opcode, 0x90 | 0x92 | 0x94 | 0x96 | 0x97)
                };
                let ty = if explicit.is_some() || reference {
                    element.stack_type()
                } else {
                    default.stack_type()
                };
                let value = b.value(
                    ty,
                    Op::LoadMemory {
                        address: address.0,
                        bits: element.bytes() * 8,
                        signed,
                    },
                )?;
                b.stack.push(value);
            }
        }
        0x28 | 0x6f | 0x73 => {
            let constructing = i.opcode == 0x73;
            let resolver = resolver.ok_or_else(|| {
                format!(
                    "CIL opcode 0x{:04x} requires assembly reference resolution",
                    i.opcode
                )
            })?;
            let token = match i.operand {
                Operand::Token(token) => token,
                _ => return Err("Missing call method token".into()),
            };
            let constrained = b
                .constrained_calls
                .get(&i.offset)
                .map(|&token| -> Result<Type, String> {
                    let operand = crate::generics::storage_in(
                        resolver,
                        method.identity.assembly,
                        &resolver.type_signature(method.identity.assembly, token)?,
                        &b.method_arguments,
                        &b.type_arguments,
                    )?;
                    let ty = bound_type(resolver, operand.assembly, &operand.ty)?;
                    if !matches!(ty, Type::Struct(_) | Type::Object(_)) {
                        return Err(
                            "Constrained receiver shape requires additional runtime lowering"
                                .into(),
                        );
                    }
                    Ok(ty)
                })
                .transpose()?;
            if constrained.is_none()
                && let Some((shape, kind)) =
                    resolver.array_method(method.identity.assembly, token)?
            {
                use crate::resolve::ArrayMethod;
                let element = bound_storage(
                    resolver,
                    method.identity.assembly,
                    rectangle_element(&shape)?,
                )?;
                if constructing != matches!(kind, ArrayMethod::Construct) {
                    return Err(
                        "Array constructor requires newobj; array access requires an instance call"
                            .into(),
                    );
                }
                let stored = if matches!(kind, ArrayMethod::Set) {
                    Some(b.pop()?)
                } else {
                    None
                };
                let column = b.pop()?;
                let row = b.pop()?;
                let row = b.coerce(row, Type::I32)?;
                let column = b.coerce(column, Type::I32)?;
                if constructing {
                    let array = b.value(
                        Type::Array(element, ArrayKind::Rectangle),
                        Op::NewRectangle {
                            element,
                            rows: row.0,
                            columns: column.0,
                        },
                    )?;
                    b.stack.push(array);
                } else {
                    let array = b.pop()?;
                    let array = b.coerce(array, Type::Array(element, ArrayKind::Rectangle))?;
                    let address = b.value(
                        element.reference_type(),
                        Op::RectangleAddress {
                            array: array.0,
                            row: row.0,
                            column: column.0,
                        },
                    )?;
                    if let Some(stored) = stored {
                        let stored = b.coerce(stored, element.stack_type())?;
                        b.instructions.push(Instruction {
                            result: None,
                            op: Op::StoreMemory {
                                address: address.0,
                                value: stored.0,
                                bits: element.bytes() * 8,
                            },
                            il_offset: b.offset,
                        });
                    } else if matches!(kind, ArrayMethod::Address) {
                        b.stack.push(address);
                    } else {
                        let value = b.value(
                            element.stack_type(),
                            Op::LoadMemory {
                                address: address.0,
                                bits: element.bytes() * 8,
                                signed: matches!(element, Storage::Scalar(value) if value.signed()),
                            },
                        )?;
                        b.stack.push(value);
                    }
                }
                return Ok(Control::Jump(b.edge(next()?)));
            }
            if constrained.is_none()
                && let Some(query) = resolver.array_query(method.identity.assembly, token)?
            {
                use crate::resolve::ArrayQuery;
                if constructing {
                    return Err("System.Array query cannot be constructed".into());
                }
                let dimension = if matches!(
                    query,
                    ArrayQuery::Dimension
                        | ArrayQuery::LongDimension
                        | ArrayQuery::LowerBound
                        | ArrayQuery::UpperBound
                ) {
                    let dimension = b.pop()?;
                    Some(b.coerce(dimension, Type::I32)?)
                } else {
                    None
                };
                let array = b.pop()?;
                let array = if array.1 == Type::Null {
                    b.coerce(
                        array,
                        Type::Array(Storage::Scalar(Scalar::U8), ArrayKind::Vector),
                    )?
                } else {
                    array
                };
                let Type::Array(_, kind) = array.1 else {
                    return Err("System.Array query requires a typed array receiver".into());
                };
                let value = if let Some(dimension) = dimension {
                    b.value(
                        Type::I32,
                        Op::ArrayDimension {
                            array: array.0,
                            dimension: dimension.0,
                        },
                    )?
                } else {
                    let count = b.value(Type::Native, Op::ArrayLength(array.0))?;
                    b.value(
                        Type::I32,
                        Op::Convert {
                            value: count.0,
                            bits: 16,
                            signed: false,
                        },
                    )?
                };
                let value = match query {
                    ArrayQuery::Rank => b.value(Type::I32, Op::Constant(u64::from(kind.rank())))?,
                    ArrayQuery::LowerBound => b.value(Type::I32, Op::Constant(0))?,
                    ArrayQuery::UpperBound => {
                        let one = b.value(Type::I32, Op::Constant(1))?;
                        b.value(
                            Type::I32,
                            Op::Binary {
                                op: Binary::Subtract,
                                left: value.0,
                                right: one.0,
                            },
                        )?
                    }
                    ArrayQuery::LongLength | ArrayQuery::LongDimension => b.value(
                        Type::I64,
                        Op::Convert {
                            value: value.0,
                            bits: 32,
                            signed: false,
                        },
                    )?,
                    _ => value,
                };
                b.stack.push(value);
                return Ok(Control::Jump(b.edge(next()?)));
            }
            if constrained.is_none()
                && resolver.object_constructor(method.identity.assembly, token)?
            {
                if constructing {
                    let object = b.value(
                        Type::Object(ClassType::root()),
                        Op::NewObject(ClassType::root()),
                    )?;
                    b.stack.push(object);
                } else if i.opcode == 0x28 {
                    if !matches!(b.pop()?.1, Type::Object(_)) {
                        return Err("Object constructor requires an object receiver".into());
                    }
                } else {
                    return Err("Object constructor requires call or newobj".into());
                }
                return Ok(Control::Jump(b.edge(next()?)));
            }
            if !constructing && constrained.is_none() {
                if let Some(indexed) = resolver.string_member(method.identity.assembly, token)? {
                    let index = if indexed {
                        let value = b.pop()?;
                        if value.1 != Type::I32 {
                            return Err("String index must be int32".into());
                        }
                        Some(value.0)
                    } else {
                        None
                    };
                    let string = b.pop()?;
                    let string = b.coerce(string, Type::String)?;
                    let op = match index {
                        Some(index) => Op::StringChar {
                            string: string.0,
                            index,
                        },
                        None => Op::StringLength(string.0),
                    };
                    let value = b.value(Type::I32, op)?;
                    b.stack.push(value);
                    return Ok(Control::Jump(b.edge(next()?)));
                }
            }
            if !constructing
                && constrained.is_none()
                && resolver.array_initializer(method.identity.assembly, token)?
            {
                let handle = b.pop()?;
                let Type::FieldHandle(field) = handle.1 else {
                    return Err("InitializeArray requires a known RVA field handle".into());
                };
                let array = b.pop()?;
                if !matches!(array.1, Type::Array(Storage::Scalar(_), _) | Type::Null) {
                    return Err("InitializeArray requires a primitive array".into());
                }
                if matches!(
                    array.1,
                    Type::Array(
                        Storage::Scalar(Scalar::NativeSigned | Scalar::NativeUnsigned),
                        _
                    )
                ) {
                    return Err(
                        "Native-width RVA array initialization is not portable to SM83".into(),
                    );
                }
                let bytes = resolver.assembly(field.assembly)?.field_data(field.token)?;
                b.data_bytes += bytes.len();
                if b.data_bytes > 65536 {
                    return Err("Method RVA initializer data exceeds 64 KiB budget".into());
                }
                b.instructions.push(Instruction {
                    result: None,
                    op: Op::InitializeArray {
                        array: array.0,
                        bytes: bytes.to_vec(),
                    },
                    il_offset: b.offset,
                });
                return Ok(Control::Jump(b.edge(next()?)));
            }
            if constrained.is_none()
                && let Some((kind, constructor, mut signature)) =
                    crate::delegates::member(resolver, method.identity.assembly, token)?
            {
                if constructor != constructing {
                    return Err(
                        "Delegate constructor requires newobj; Invoke requires call/callvirt"
                            .into(),
                    );
                }
                if constructor {
                    let pointer = b.pop()?;
                    let Type::MethodHandle(target) = pointer.1 else {
                        return Err("Delegate constructor requires a typed method handle".into());
                    };
                    let receiver = b.pop()?;
                    if b.delegate_sequences
                        .get(&b.offset)
                        .is_none_or(|virtual_sequence| {
                            crate::dispatch::is_dispatch(target) && !virtual_sequence
                        })
                    {
                        return Err("Delegate construction requires a verified ldftn/newobj or dup/ldvirtftn/newobj sequence without branches into its interior".into());
                    }
                    let declared = if crate::dispatch::is_dispatch(target) {
                        crate::dispatch::declaration(target)
                    } else {
                        target
                    };
                    let bound = declared;
                    let declared = resolver
                        .instance(declared)?
                        .map_or(declared, |instance| instance.method);
                    let instance = resolver
                        .assembly(declared.assembly)?
                        .method_signature(declared.token)?
                        .calling_convention
                        .has_this();
                    if !instance && receiver.1 != Type::Null {
                        return Err(
                            "Closed static delegates require explicit argument binding".into()
                        );
                    }
                    if instance && !matches!(receiver.1, Type::Object(_) | Type::Null) {
                        return Err("Instance delegate requires an object target".into());
                    }
                    let target = crate::delegates::callback(resolver, kind, target)?;
                    let receiver = if instance {
                        let owner = owner_type(resolver, bound)?;
                        let receiver =
                            b.coerce(receiver, Type::Object(class_type(resolver, owner)?))?;
                        b.instructions.push(Instruction {
                            result: None,
                            op: Op::CheckObject(receiver.0),
                            il_offset: b.offset,
                        });
                        receiver
                    } else {
                        receiver
                    };
                    let receiver = b.coerce(receiver, Type::Object(ClassType::root()))?;
                    signature
                        .parameters
                        .insert(0, Type::Object(ClassType::root()));
                    let value = b.value(
                        Type::Delegate(kind),
                        Op::NewDelegate {
                            kind,
                            target,
                            signature,
                            receiver: receiver.0,
                        },
                    )?;
                    b.stack.push(value);
                } else {
                    signature.parameters.insert(0, Type::Delegate(kind));
                    let mut arguments = Vec::with_capacity(signature.parameters.len());
                    for &ty in signature.parameters.iter().rev() {
                        let value = b.pop()?;
                        arguments.push(b.coerce(value, ty)?.0);
                    }
                    arguments.reverse();
                    let op = Op::InvokeDelegate {
                        signature: signature.clone(),
                        arguments,
                    };
                    if let Some(ty) = signature.returns {
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
                return Ok(Control::Jump(b.edge(next()?)));
            }
            if constrained.is_none()
                && let Some(crate::generics::Construction { ty, constructor }) = resolver
                    .construction(
                        method.identity.assembly,
                        token,
                        &b.method_arguments,
                        &b.type_arguments,
                    )?
            {
                if i.opcode != 0x28 {
                    return Err("Activator.CreateInstance requires static call".into());
                }
                if let Some(target) = constructor {
                    let origin = resolver
                        .instance(target)?
                        .map_or(target, |instance| instance.method);
                    let signature = crate::generics::method_types(
                        resolver,
                        target,
                        &resolver
                            .assembly(origin.assembly)?
                            .method_signature(origin.token)?,
                        &[],
                    )?
                    .signature;
                    construct(b, ty, target, signature, vec![], locals.len())?;
                } else {
                    let op = match ty {
                        Type::Object(class) => Op::NewObject(class),
                        Type::Struct(_) => Op::ZeroStruct,
                        _ => Op::Constant(0),
                    };
                    let value = b.value(ty, op)?;
                    b.stack.push(value);
                }
                return Ok(Control::Jump(b.edge(next()?)));
            }
            let mut target = resolver.resolve_method_in(
                method.identity.assembly,
                token,
                &b.method_arguments,
                &b.type_arguments,
            )?;
            let constrained_value = if let Some(Type::Struct(value)) = constrained {
                let declaration = resolver.instantiate_in(
                    &target,
                    &b.method_arguments,
                    &b.type_arguments,
                    b.declaring,
                )?;
                target.definition =
                    crate::constrained::value_target(resolver, value.identity(), declaration)?;
                target.type_arguments.clear();
                target.declaring = resolver
                    .type_instance(value.identity())?
                    .map(|_| value.identity());
                true
            } else {
                false
            };
            let instantiated = resolver.instantiate_in(
                &target,
                &b.method_arguments,
                &b.type_arguments,
                b.declaring,
            )?;
            let metadata = resolver.assembly(target.definition.assembly)?;
            if i.opcode == 0x6f {
                let signature = metadata.method_signature(target.definition.token)?;
                if !signature.calling_convention.has_this() {
                    return Err("callvirt requires an instance method".into());
                }
            }
            if constructing {
                let definition = metadata.method(target.definition.token)?;
                let signature = metadata.method_signature(target.definition.token)?;
                if metadata
                    .metadata()
                    .strings
                    .get(definition.name)
                    .map_err(|e| e.to_string())?
                    != ".ctor"
                    || definition.flags & 0x1810 != 0x1800
                    || !signature.calling_convention.has_this()
                    || signature.generic_param_count != 0
                    || signature.return_type
                        != clrmeta::TypeSig::Primitive(clrmeta::ElementType::Void)
                {
                    return Err(
                        "newobj requires a nongeneric instance void .ctor with constructor flags"
                            .into(),
                    );
                }
            }
            if metadata
                .metadata()
                .strings
                .get(metadata.method(target.definition.token)?.name)
                .map_err(|e| e.to_string())?
                == ".cctor"
            {
                return Err("Direct CIL calls to type/module constructors are unsupported".into());
            }
            let instance = resolver.instance(instantiated)?;
            if spans::call(resolver, instantiated, constructing, b)? {
                return Ok(Control::Jump(b.edge(next()?)));
            }
            let method_types = crate::generics::method_types(
                resolver,
                instantiated,
                &metadata.method_signature(target.definition.token)?,
                instance
                    .as_ref()
                    .map_or(&[][..], |instance| instance.arguments.as_slice()),
            )?;
            let return_narrow = narrow_type(&method_types.returns.ty);
            let signature = method_types.signature;
            let mut arguments = Vec::with_capacity(signature.parameters.len());
            let constructed = if constructing {
                Some(match signature.parameters.first() {
                    Some(Type::StructRef(shape)) => Type::Struct(*shape),
                    Some(Type::Object(class)) => {
                        let owner = owner_type(resolver, target.definition)?;
                        if metadata.metadata().type_defs[(owner.token & 0xffffff) as usize - 1]
                            .flags
                            & 0x80
                            != 0
                        {
                            return Err("newobj cannot instantiate an abstract class".into());
                        }
                        Type::Object(*class)
                    }
                    _ => return Err("newobj requires a value or reference constructor".into()),
                })
            } else {
                None
            };
            for (index, &ty) in signature
                .parameters
                .iter()
                .enumerate()
                .skip(usize::from(constructing))
                .rev()
            {
                let mut value = b.pop()?;
                if index == 0 {
                    if let Some(prefix) = constrained {
                        match prefix {
                            Type::Object(class)
                                if value.1 == Type::ReferenceCell(ReferenceCell::Object(class)) =>
                            {
                                value = b.value(
                                    prefix,
                                    Op::LoadMemory {
                                        address: value.0,
                                        bits: 16,
                                        signed: false,
                                    },
                                )?;
                            }
                            Type::Struct(shape) if value.1 == Type::StructRef(shape) => {}
                            _ => return Err(
                                "Constrained receiver must be a managed pointer to its closed type"
                                    .into(),
                            ),
                        }
                    }
                }
                let value = b.coerce(value, ty)?;
                arguments.push(value.0);
            }
            arguments.reverse();
            if int128::call(resolver, target.definition, &arguments, &signature, b)? {
                return Ok(Control::Jump(b.edge(next()?)));
            }
            if let Some(intrinsic) = crate::contracts::intrinsic(resolver, target.definition)? {
                if constructing {
                    return Err("newobj cannot invoke a hardware intrinsic".into());
                }
                use crate::contracts::Intrinsic;
                let address = |b: &mut Builder, address: u16| {
                    b.value(Type::Native, Op::Constant(u64::from(address)))
                };
                match intrinsic {
                    Intrinsic::HBlankStream(register) => b.instructions.push(Instruction {
                        result: None,
                        op: Op::HBlankStream {
                            source: arguments[0],
                            first: arguments[1],
                            count: arguments[2],
                            register,
                        },
                        il_offset: b.offset,
                    }),
                    Intrinsic::VramWrite => b.instructions.push(Instruction {
                        result: None,
                        op: Op::VramWrite {
                            address: arguments[0],
                            value: arguments[1],
                        },
                        il_offset: b.offset,
                    }),
                    Intrinsic::MemoryFill => b.instructions.push(Instruction {
                        result: None,
                        op: Op::MemoryFill {
                            address: arguments[0],
                            value: arguments[1],
                            count: arguments[2],
                        },
                        il_offset: b.offset,
                    }),
                    Intrinsic::MemoryCopy => b.instructions.push(Instruction {
                        result: None,
                        op: Op::MemoryCopy {
                            destination: arguments[0],
                            source: arguments[1],
                            count: arguments[2],
                        },
                        il_offset: b.offset,
                    }),
                    Intrinsic::VBlankCopy => {
                        let copied = b.value(
                            Type::I32,
                            Op::VBlankCopy {
                                destination: arguments[0],
                                source: arguments[1],
                                count: arguments[2],
                            },
                        )?;
                        b.stack.push(copied);
                    }
                    Intrinsic::Allocate => {
                        let pointer = b.value(Type::Native, Op::ArenaAllocate(arguments[0]))?;
                        b.stack.push(pointer);
                    }
                    Intrinsic::ResetArena => b.instructions.push(Instruction {
                        result: None,
                        op: Op::ArenaReset,
                        il_offset: b.offset,
                    }),
                    Intrinsic::CpuControl(control) => b.instructions.push(Instruction {
                        result: None,
                        op: Op::CpuControl(control),
                        il_offset: b.offset,
                    }),
                    Intrinsic::OamDma => b.instructions.push(Instruction {
                        result: None,
                        op: Op::OamDma(arguments[0]),
                        il_offset: b.offset,
                    }),
                    Intrinsic::Read(a) => {
                        let pointer = address(b, a)?;
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
                    Intrinsic::Write(a) => {
                        let pointer = address(b, a)?;
                        b.instructions.push(Instruction {
                            result: None,
                            op: Op::StoreMemory {
                                address: pointer.0,
                                value: arguments[0],
                                bits: 8,
                            },
                            il_offset: b.offset,
                        });
                    }
                    Intrinsic::Region(a) => {
                        let pointer = address(b, a)?;
                        b.stack.push(pointer);
                    }
                }
                return Ok(Control::Jump(b.edge(next()?)));
            }
            if let Some(ty) = constructed {
                construct(b, ty, instantiated, signature, arguments, locals.len())?;
                return Ok(Control::Jump(b.edge(next()?)));
            }
            let returns = signature.returns;
            if i.opcode == 0x6f && !constrained_value {
                let receiver = *arguments.first().ok_or("Missing callvirt receiver")?;
                b.instructions.push(Instruction {
                    result: None,
                    op: Op::CheckObject(receiver),
                    il_offset: b.offset,
                });
            }
            let op = Op::Call {
                target: if i.opcode == 0x6f
                    && !constrained_value
                    && metadata.method(target.definition.token)?.flags & 0x40 != 0
                {
                    crate::dispatch::identity(instantiated)
                } else {
                    instantiated
                },
                signature,
                arguments,
            };
            if let Some(ty) = returns {
                let mut value = b.value(ty, op)?;
                if let Some((bits, signed)) = return_narrow {
                    value = b.value(
                        Type::I32,
                        Op::Convert {
                            value: value.0,
                            bits,
                            signed,
                        },
                    )?;
                }
                b.stack.push(value);
            } else {
                b.instructions.push(Instruction {
                    result: None,
                    op,
                    il_offset: b.offset,
                });
            }
        }
        0x00 | 0xfe16 => {}
        0x02..=0x05 => {
            let index = usize::from(i.opcode - 0x02);
            if index >= method.parameters.len() {
                return Err("Invalid argument index".into());
            }
            load(b, index)?;
        }
        0x06..=0x09 => load(b, method.parameters.len() + usize::from(i.opcode - 0x06))?,
        0x0a..=0x0d => store(b, method.parameters.len() + usize::from(i.opcode - 0x0a))?,
        0x0f | 0xfe0a | 0x12 | 0xfe0d => {
            let argument = matches!(i.opcode, 0x0f | 0xfe0a);
            let raw = variable()?;
            if argument && raw >= method.parameters.len() {
                return Err("Invalid argument address index".into());
            }
            let index = if argument {
                raw
            } else {
                method.parameters.len() + raw
            };
            let ty = *locals.get(index).ok_or("Invalid local address index")?;
            let storage = if let Type::Struct(value) = ty {
                FieldStorage::Value(Storage::Struct(value))
            } else if let Some(resolver) = resolver {
                let image = resolver.assembly(method.identity.assembly)?;
                let types = if argument {
                    crate::generics::method_types(
                        resolver,
                        if b.declaring.is_some() {
                            resolver.specialize_on(
                                method.identity,
                                b.method_arguments.clone(),
                                b.declaring,
                            )?
                        } else {
                            method.identity
                        },
                        &image.method_signature(method.identity.token)?,
                        &b.method_arguments,
                    )?
                    .parameters
                } else {
                    image
                        .local_types(method.identity.token)?
                        .iter()
                        .map(|ty| {
                            crate::generics::storage_in(
                                resolver,
                                method.identity.assembly,
                                ty,
                                &b.method_arguments,
                                &b.type_arguments,
                            )
                        })
                        .collect::<Result<Vec<_>, _>>()?
                };
                let ty = types
                    .get(raw)
                    .ok_or("Missing addressed storage signature")?;
                field_storage(resolver, ty.assembly, &ty.ty)?
            } else {
                FieldStorage::Value(Storage::Scalar(match ty {
                    Type::I32 => Scalar::I32,
                    Type::I64 => Scalar::I64,
                    Type::I128 => Scalar::I128,
                    Type::F32 => Scalar::F32,
                    Type::F64 => Scalar::F64,
                    Type::Native => Scalar::NativeSigned,
                    Type::ByRef(_)
                    | Type::RomByteRef
                    | Type::Delegate(_)
                    | Type::Object(_)
                    | Type::ReferenceCell(_)
                    | Type::Array(_, _)
                    | Type::String
                    | Type::Null
                    | Type::FieldHandle(_)
                    | Type::MethodHandle(_)
                    | Type::Struct(_)
                    | Type::StructRef(_) => {
                        return Err("Nested managed references are unsupported".into());
                    }
                }))
            };
            let value = b.value(
                storage.reference_type(),
                Op::LocalAddress {
                    local: index as u32,
                    storage,
                },
            )?;
            b.stack.push(value);
        }
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
        0x14 => {
            let value = b.value(Type::Null, Op::Constant(0))?;
            b.stack.push(value);
        }
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
        0x22 => {
            let Operand::Float32(bits) = i.operand else {
                return Err("Missing float32 constant".into());
            };
            let value = b.value(Type::F32, Op::Constant(u64::from(bits)))?;
            b.stack.push(value);
        }
        0x23 => {
            let Operand::Float64(bits) = i.operand else {
                return Err("Invalid float64 constant".into());
            };
            let value = b.value(Type::F64, Op::Constant(bits))?;
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
                    let value = b
                        .coerce(value, ty)
                        .map_err(|error| format!("CIL return type mismatch: {error}"))?;
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
                let (mut op, invert) = match code {
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
                if invert
                    && b.stack
                        .last()
                        .is_some_and(|value| matches!(value.1, Type::F32 | Type::F64))
                {
                    op = match op {
                        Comparison::LessSigned => Comparison::LessUnsigned,
                        Comparison::LessUnsigned => Comparison::LessSigned,
                        Comparison::GreaterSigned => Comparison::GreaterUnsigned,
                        Comparison::GreaterUnsigned => Comparison::GreaterSigned,
                        Comparison::Equal => Comparison::Equal,
                    };
                }
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
        0x7b..=0x7d => {
            let resolver = resolver.ok_or("Instance fields require metadata resolution")?;
            let Operand::Token(token) = i.operand else {
                return Err("Invalid instance field token".into());
            };
            let target = resolver.resolve_field(method.identity.assembly, token)?;
            let (field_identity, owner, _) = resolver.bind_field(
                &target,
                &b.method_arguments,
                &b.type_arguments,
                b.declaring,
            )?;
            let image = resolver.assembly(target.definition.assembly)?;
            let row = (target.definition.token & 0xffffff)
                .checked_sub(1)
                .and_then(|row| image.metadata().fields.get(row as usize))
                .ok_or("Invalid instance field definition")?;
            if row.flags & (0x10 | 0x40 | 0x100) != 0 {
                return Err("Instance field opcode requires a nonstatic value field".into());
            }
            if i.opcode == 0x7d && row.flags & 0x20 != 0 {
                let method_image = resolver.assembly(method.identity.assembly)?;
                let constructor = method_image
                    .metadata()
                    .strings
                    .get(method_image.method(method.identity.token)?.name)
                    .map_err(|e| e.to_string())?
                    == ".ctor";
                if !constructor
                    || b.declaring
                        .map_or_else(|| owner_type(resolver, method.identity), Ok)?
                        != owner
                {
                    return Err(
                        "Readonly instance field writes require declaring constructor".into(),
                    );
                }
            }
            let stored = if i.opcode == 0x7d {
                Some(b.pop()?)
            } else {
                None
            };
            let receiver = b.pop()?;
            let raw_owner = resolver.type_definition(owner)?;
            let owner_row = &image.metadata().type_defs[(raw_owner.token & 0xffffff) as usize - 1];
            let value_owner = owner_row.extends.table == Some(clrmeta::TableId::TypeRef)
                && crate::resolve::framework_type(
                    image.metadata(),
                    0x01000000 | owner_row.extends.row,
                )? == Some(("System", "ValueType"));
            if !value_owner {
                let layout = crate::layout::class(resolver, owner)?;
                let class = class_type(resolver, owner)?;
                let field = layout
                    .fields
                    .iter()
                    .find(|f| f.identity == field_identity)
                    .ok_or("Field is absent from class layout")?;
                let storage = field_storage(resolver, field.signature_assembly, &field.signature)?;
                let receiver = b.coerce(receiver, Type::Object(class))?;
                let address = b.value(
                    storage.reference_type(),
                    Op::ObjectFieldAddress {
                        object: receiver.0,
                        owner: class,
                        offset: field.offset,
                        storage,
                    },
                )?;
                if let Some(value) = stored {
                    let value = b.coerce(value, storage.stack_type())?;
                    b.instructions.push(Instruction {
                        result: None,
                        op: Op::StoreMemory {
                            address: address.0,
                            value: value.0,
                            bits: storage.bytes() * 8,
                        },
                        il_offset: b.offset,
                    });
                } else if i.opcode == 0x7c {
                    b.stack.push(address);
                } else {
                    let value = b.value(storage.stack_type(), Op::LoadMemory { address: address.0,
                        bits: storage.bytes() * 8,
                        signed: matches!(storage, FieldStorage::Value(Storage::Scalar(scalar)) if scalar.signed()) })?;
                    b.stack.push(value);
                }
                return Ok(Control::Jump(b.edge(next()?)));
            }
            let layout = crate::layout::value(resolver, owner)?;
            let shape = value_type(&layout)?;
            let field = layout
                .fields
                .iter()
                .find(|f| f.identity == field_identity)
                .ok_or("Field is not in aggregate layout")?;
            let storage = field_storage(resolver, field.signature_assembly, &field.signature)?;
            if receiver.1 == Type::Struct(shape) && i.opcode == 0x7b {
                let value = b.value(
                    storage.stack_type(),
                    Op::ExtractField {
                        value: receiver.0,
                        offset: field.offset,
                        storage,
                    },
                )?;
                b.stack.push(value);
            } else {
                if receiver.1 != Type::Native && receiver.1 != Type::StructRef(shape) {
                    return Err("Instance field receiver requires matching struct reference".into());
                }
                let address = b.value(
                    if receiver.1 == Type::Native {
                        Type::Native
                    } else {
                        storage.reference_type()
                    },
                    Op::FieldAddress {
                        address: receiver.0,
                        owner: shape,
                        offset: field.offset,
                        storage,
                    },
                )?;
                if let Some(value) = stored {
                    let value = b.coerce(value, storage.stack_type())?;
                    b.instructions.push(Instruction {
                        result: None,
                        op: Op::StoreMemory {
                            address: address.0,
                            value: value.0,
                            bits: storage.bytes() * 8,
                        },
                        il_offset: b.offset,
                    });
                } else if i.opcode == 0x7c {
                    b.stack.push(address);
                } else {
                    let value = b.value(
                        storage.stack_type(),
                        Op::LoadMemory {
                            address: address.0,
                            bits: storage.bytes() * 8,
                            signed: matches!(storage, FieldStorage::Value(Storage::Scalar(scalar)) if scalar.signed()),
                        },
                    )?;
                    b.stack.push(value);
                }
            }
        }
        0x7e..=0x80 => {
            let resolver = resolver.ok_or("Static fields require assembly resolution")?;
            let Operand::Token(token) = i.operand else {
                return Err("Missing static field token".into());
            };
            let target = resolver.resolve_field(method.identity.assembly, token)?;
            let (field_identity, _, field_signature) = resolver.bind_field(
                &target,
                &b.method_arguments,
                &b.type_arguments,
                b.declaring,
            )?;
            let image = resolver.assembly(target.definition.assembly)?;
            let field = &image.metadata().fields[(target.definition.token & 0xffffff) as usize - 1];
            if i.opcode == 0x7f && field.flags & 0x110 == 0x110 {
                image.field_data(target.definition.token)?;
                let value = b.value(
                    Type::FieldHandle(target.definition),
                    Op::FieldHandle(target.definition),
                )?;
                b.stack.push(value);
                return Ok(Control::Jump(b.edge(next()?)));
            }
            if field.flags & 0x10 == 0 || field.flags & (0x40 | 0x100) != 0 {
                return Err("Static field flags require literal/RVA/instance lowering".into());
            }
            let initializer = resolver.field_initializer(field_identity)?;
            let current = if b.declaring.is_some() {
                resolver.specialize_on(method.identity, b.method_arguments.clone(), b.declaring)?
            } else {
                method.identity
            };
            if i.opcode == 0x80 && field.flags & 0x20 != 0 && initializer != Some(current) {
                return Err(
                    "Readonly static field writes require their declaring constructor".into(),
                );
            }
            for constructor in [
                resolver.module_initializer(target.definition.assembly)?,
                initializer,
            ]
            .into_iter()
            .flatten()
            {
                if constructor != current {
                    b.instructions.push(Instruction {
                        result: None,
                        op: crate::initialize::guard(constructor),
                        il_offset: b.offset,
                    });
                }
            }
            let signature = storage_type(resolver, field_signature.assembly, &field_signature.ty)?;
            if matches!(signature, clrmeta::TypeSig::ByRef(_)) {
                return Err("Static managed-reference storage is unsupported".into());
            }
            let ty = bound_type(resolver, field_signature.assembly, &signature)?;
            let alignment = crate::contracts::field_alignment(resolver, target.definition)?;
            let (bits, signed) = narrow_type(&signature)
                .map(|(bits, signed)| (u16::from(bits), signed))
                .unwrap_or((ty.bits(), !matches!(ty, Type::Struct(_))));
            let address_type = if let Type::Struct(value) = ty {
                Type::StructRef(value)
            } else if let Type::Object(class) = ty {
                Type::ReferenceCell(ReferenceCell::Object(class))
            } else if ty == Type::String {
                Type::ReferenceCell(ReferenceCell::String)
            } else if let Type::Delegate(kind) = ty {
                Type::ReferenceCell(ReferenceCell::Delegate(kind))
            } else if let Type::Array(element, kind) = ty {
                Type::ReferenceCell(ReferenceCell::Array(element, kind))
            } else if i.opcode == 0x7f {
                Type::ByRef(scalar(&signature)?)
            } else {
                Type::Native
            };
            let address = b.value(
                address_type,
                Op::StaticAddress {
                    field: field_identity,
                    bytes: bits / 8,
                    alignment: if matches!(ty, Type::Array(_, _)) {
                        1
                    } else {
                        alignment
                    },
                },
            )?;
            if i.opcode == 0x80 {
                let value = b.pop()?;
                let value = b.coerce(value, ty)?;
                b.instructions.push(Instruction {
                    result: None,
                    op: Op::StoreMemory {
                        address: address.0,
                        value: value.0,
                        bits,
                    },
                    il_offset: b.offset,
                });
            } else if i.opcode == 0x7f {
                b.stack.push(address);
            } else {
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
        }
        0x50 | 0x51 => {
            let stored = if i.opcode == 0x51 {
                Some(b.pop()?)
            } else {
                None
            };
            let address = b.pop()?;
            let Type::ReferenceCell(cell) = address.1 else {
                return Err("Indirect reference access requires a typed reference cell".into());
            };
            if let Some(value) = stored {
                let value = b.coerce(value, cell.stack_type())?;
                b.instructions.push(Instruction {
                    result: None,
                    op: Op::StoreMemory {
                        address: address.0,
                        value: value.0,
                        bits: 16,
                    },
                    il_offset: b.offset,
                });
            } else {
                let value = b.value(
                    cell.stack_type(),
                    Op::LoadMemory {
                        address: address.0,
                        bits: 16,
                        signed: false,
                    },
                )?;
                b.stack.push(value);
            }
        }
        0x46..=0x4f => {
            let (bits, signed, ty) = match i.opcode {
                0x46 => (8, true, Type::I32),
                0x47 => (8, false, Type::I32),
                0x48 => (16, true, Type::I32),
                0x49 => (16, false, Type::I32),
                0x4a => (32, true, Type::I32),
                0x4b => (32, false, Type::I32),
                0x4c => (64, true, Type::I64),
                0x4e => (32, false, Type::F32),
                0x4f => (64, false, Type::F64),
                _ => (16, false, Type::Native),
            };
            let address = b.pop()?;
            if !address.1.addresses(bits) {
                return Err(
                    "Indirect load requires native address or matching primitive reference".into(),
                );
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
        0x52..=0x57 | 0xdf => {
            let (bits, ty) = match i.opcode {
                0x52 => (8, Type::I32),
                0x53 => (16, Type::I32),
                0x54 => (32, Type::I32),
                0x55 => (64, Type::I64),
                0x56 => (32, Type::F32),
                0x57 => (64, Type::F64),
                _ => (16, Type::Native),
            };
            let value = b.pop()?;
            let address = b.pop()?;
            if !address.1.addresses(bits) || value.1 != ty {
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
        0xd6..=0xdb => checked::arithmetic(b, i.opcode)?,
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
            if matches!(value.1, Type::F32 | Type::F64) {
                if i.opcode != 0x65 {
                    return Err("Bitwise not cannot consume floating values".into());
                }
                let raw = if value.1 == Type::F64 {
                    Type::I64
                } else {
                    Type::I32
                };
                let bits = b.value(raw, Op::Reinterpret(value.0))?;
                let sign = b.value(raw, Op::Constant(1u64 << (value.1.bits() - 1)))?;
                let bits = b.value(
                    raw,
                    Op::Binary {
                        op: Binary::Xor,
                        left: bits.0,
                        right: sign.0,
                    },
                )?;
                let value = b.value(value.1, Op::Reinterpret(bits.0))?;
                b.stack.push(value);
            } else {
                let op = if i.opcode == 0x65 {
                    Op::Negate(value.0)
                } else {
                    Op::Not(value.0)
                };
                let value = b.value(value.1, op)?;
                b.stack.push(value);
            }
        }
        0x6c => {
            let value = b.pop()?;
            let value = match value.1 {
                Type::F64 => value,
                Type::F32 => {
                    let bits = b.float_conversion("f32.toF64", value)?;
                    b.value(Type::F64, Op::Reinterpret(bits.0))?
                }
                Type::I32 | Type::I64 | Type::Native => {
                    let bits = b.float_conversion("i64.toF64", value)?;
                    b.value(Type::F64, Op::Reinterpret(bits.0))?
                }
                _ => return Err("Floating conversion requires a numeric operand".into()),
            };
            b.stack.push(value);
        }
        0x6b | 0x76 => {
            let value = b.pop()?;
            let precision =
                if i.opcode == 0x76 && matches!(value.1, Type::I32 | Type::I64 | Type::Native) {
                    match method
                        .il
                        .get(i.offset as usize..)
                        .and_then(|bytes| bytes.iter().skip(1).find(|&&byte| byte != 0))
                    {
                        Some(0x6b) => Type::F32,
                        Some(0x6c) => Type::F64,
                        _ => {
                            return Err(
                                "conv.r.un requires explicit conv.r4 or conv.r8 precision".into()
                            );
                        }
                    }
                } else {
                    Type::F32
                };
            let value = if value.1 == Type::F32 && i.opcode == 0x6b {
                value
            } else if value.1 == Type::F64 && i.opcode == 0x6b {
                let bits = b.float_conversion("f64.toF32", value)?;
                b.value(Type::F32, Op::Reinterpret(bits.0))?
            } else {
                match value.1 {
                    Type::I32 | Type::I64 | Type::Native => {}
                    Type::F32 | Type::F64 => {
                        return Err("conv.r.un requires an integer operand".into());
                    }
                    _ => {
                        return Err(
                            "Floating conversion requires a supported integer operand".into()
                        );
                    }
                }
                let key = if precision == Type::F64 {
                    "u64.toF64"
                } else {
                    match (value.1 == Type::I64, i.opcode == 0x76) {
                        (true, true) => "u64.toF32",
                        (true, false) => "i64.toF32",
                        (false, true) => "u32.toF32",
                        (false, false) => "i32.toF32",
                    }
                };
                let bits = b.float_conversion(key, value)?;
                b.value(precision, Op::Reinterpret(bits.0))?
            };
            b.stack.push(value);
        }
        0x82..=0x8b | 0xb3..=0xba | 0xd4..=0xd5 => {
            let value = b.pop()?;
            let converted = checked::convert(b, value, i.opcode)?;
            b.stack.push(converted);
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
            let mut value = b.pop()?;
            if matches!(value.1, Type::F32 | Type::F64) {
                let key = match (value.1, bits == 64, signed) {
                    (Type::F32, true, true) => "f32.toI64",
                    (Type::F32, true, false) => "f32.toU64",
                    (Type::F32, false, true) => "f32.toI32",
                    (Type::F32, false, false) => "f32.toU32",
                    (Type::F64, true, true) => "f64.toI64",
                    (Type::F64, true, false) => "f64.toU64",
                    (Type::F64, false, true) => "f64.toI32",
                    (Type::F64, false, false) => "f64.toU32",
                    _ => return Err("Invalid floating integer conversion type".into()),
                };
                value = b.float_conversion(key, value)?;
            }
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
