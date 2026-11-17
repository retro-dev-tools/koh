use super::*;
use crate::resolve::Resolver;

pub(super) fn scalar(
    resolver: &Resolver<'_>,
    assembly: usize,
    ty: &clrmeta::TypeSig,
) -> Result<Option<Scalar>, String> {
    let clrmeta::TypeSig::ValueType(encoded) = ty else {
        return Ok(None);
    };
    let definition = resolver.signature_type(assembly, *encoded)?;
    Ok(match resolver.framework_definition(definition)? {
        Some(("System", "Int128")) => Some(Scalar::I128),
        Some(("System", "UInt128")) => Some(Scalar::U128),
        _ => None,
    })
}

pub(super) fn call(
    resolver: &Resolver<'_>,
    target: Definition,
    arguments: &[u32],
    signature: &Signature,
    b: &mut Builder,
) -> Result<bool, String> {
    let owner = owner_type(resolver, target)?;
    let signed = match resolver.framework_definition(owner)? {
        Some(("System", "Int128")) => true,
        Some(("System", "UInt128")) => false,
        _ => return Ok(false),
    };
    let image = resolver.assembly(target.assembly)?;
    let name = image
        .metadata()
        .strings
        .get(image.method(target.token)?.name)
        .map_err(|e| e.to_string())?;
    let binary = match name {
        "op_Addition" => Some(Binary::Add),
        "op_Subtraction" => Some(Binary::Subtract),
        "op_Multiply" => Some(Binary::Multiply),
        "op_Division" => Some(if signed {
            Binary::DivideSigned
        } else {
            Binary::DivideUnsigned
        }),
        "op_Modulus" => Some(if signed {
            Binary::RemainderSigned
        } else {
            Binary::RemainderUnsigned
        }),
        "op_BitwiseAnd" => Some(Binary::And),
        "op_BitwiseOr" => Some(Binary::Or),
        "op_ExclusiveOr" => Some(Binary::Xor),
        "op_LeftShift" => Some(Binary::ShiftLeft),
        "op_RightShift" => Some(if signed {
            Binary::ShiftRightSigned
        } else {
            Binary::ShiftRightUnsigned
        }),
        "op_UnsignedRightShift" => Some(Binary::ShiftRightUnsigned),
        _ => None,
    };
    let result = if let Some(op) = binary {
        let shift = matches!(
            op,
            Binary::ShiftLeft | Binary::ShiftRightSigned | Binary::ShiftRightUnsigned
        );
        let right = if shift { Type::I32 } else { Type::I128 };
        if arguments.len() != 2
            || signature.parameters != [Type::I128, right]
            || signature.returns != Some(Type::I128)
        {
            return Err(format!("Invalid 128-bit operator signature: {name}"));
        }
        b.value(
            Type::I128,
            Op::Binary {
                op,
                left: arguments[0],
                right: arguments[1],
            },
        )?
    } else if matches!(name, "op_Implicit" | "op_Explicit") && arguments.len() == 1 {
        let source = signature.parameters[0];
        let destination = signature
            .returns
            .ok_or("128-bit conversion requires a result")?;
        if !matches!(source, Type::I32 | Type::I64 | Type::I128 | Type::Native)
            || !matches!(
                destination,
                Type::I32 | Type::I64 | Type::I128 | Type::Native
            )
        {
            return Err("128-bit floating-point conversion is unsupported".into());
        }
        let raw = image.method_signature(target.token)?;
        let input = &raw.params[0];
        let source_signed = scalar(resolver, target.assembly, input)?
            .or_else(|| super::scalar(input).ok())
            .is_some_and(Scalar::signed);
        let output = &raw.return_type;
        let output_scalar = scalar(resolver, target.assembly, output)?
            .or_else(|| super::scalar(output).ok())
            .ok_or("Invalid 128-bit conversion result")?;
        b.value(
            destination,
            Op::Convert {
                value: arguments[0],
                bits: output_scalar.bits(),
                signed: if destination == Type::I128 {
                    source_signed
                } else {
                    output_scalar.signed()
                },
            },
        )?
    } else if arguments.len() == 1
        && signature.parameters == [Type::I128]
        && signature.returns == Some(Type::I128)
    {
        match name {
            "op_UnaryNegation" => b.value(Type::I128, Op::Negate(arguments[0]))?,
            "op_OnesComplement" => b.value(Type::I128, Op::Not(arguments[0]))?,
            "op_UnaryPlus" => (arguments[0], Type::I128),
            _ => return Err(format!("Unsupported 128-bit member: {name}")),
        }
    } else if arguments.len() == 2
        && signature.parameters == [Type::I128, Type::I128]
        && signature.returns == Some(Type::I32)
    {
        let (op, invert) = match name {
            "op_Equality" => (Comparison::Equal, false),
            "op_Inequality" => (Comparison::Equal, true),
            "op_LessThan" | "op_GreaterThanOrEqual" => (
                if signed {
                    Comparison::LessSigned
                } else {
                    Comparison::LessUnsigned
                },
                name == "op_GreaterThanOrEqual",
            ),
            "op_GreaterThan" | "op_LessThanOrEqual" => (
                if signed {
                    Comparison::GreaterSigned
                } else {
                    Comparison::GreaterUnsigned
                },
                name == "op_LessThanOrEqual",
            ),
            _ => return Err(format!("Unsupported 128-bit member: {name}")),
        };
        let value = b.value(
            Type::I32,
            Op::Compare {
                op,
                left: arguments[0],
                right: arguments[1],
            },
        )?;
        if invert {
            let zero = b.value(Type::I32, Op::Constant(0))?;
            b.value(
                Type::I32,
                Op::Compare {
                    op: Comparison::Equal,
                    left: value.0,
                    right: zero.0,
                },
            )?
        } else {
            value
        }
    } else {
        return Err(format!("Unsupported 128-bit member: {name}"));
    };
    b.stack.push(result);
    Ok(true)
}
