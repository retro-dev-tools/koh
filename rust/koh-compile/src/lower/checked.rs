use super::*;

pub(super) fn arithmetic(b: &mut Builder<'_, '_>, opcode: u16) -> Result<(), String> {
    let mut right = b.pop()?;
    let mut left = b.pop()?;
    let unsigned = matches!(opcode, 0xd7 | 0xdb);
    let subtract = matches!(opcode, 0xda | 0xdb);
    if left.1.is_byref() || right.1.is_byref() {
        return Err("Checked managed-reference arithmetic is unsupported".into());
    }
    if !matches!(left.1, Type::I32 | Type::I64 | Type::Native)
        || !matches!(right.1, Type::I32 | Type::I64 | Type::Native)
    {
        return Err("Checked arithmetic requires integer operands".into());
    }
    let mixed = matches!(
        (left.1, right.1),
        (Type::Native, Type::I32) | (Type::I32, Type::Native)
    );
    if mixed {
        for value in [&mut left, &mut right] {
            if value.1 == Type::Native {
                *value = b.value(
                    Type::I32,
                    Op::Convert {
                        value: value.0,
                        bits: 16,
                        signed: !unsigned,
                    },
                )?;
            }
        }
    }
    if left.1 != right.1 {
        return Err("Checked arithmetic operand type mismatch".into());
    }
    let result = b.value(
        left.1,
        Op::Binary {
            op: if subtract {
                Binary::Subtract
            } else {
                Binary::Add
            },
            left: left.0,
            right: right.0,
        },
    )?;
    let overflow = if unsigned {
        b.value(
            Type::I32,
            Op::Compare {
                op: Comparison::LessUnsigned,
                left: if subtract { left.0 } else { result.0 },
                right: if subtract { right.0 } else { left.0 },
            },
        )?
    } else {
        let x = b.value(
            left.1,
            Op::Binary {
                op: Binary::Xor,
                left: left.0,
                right: if subtract { right.0 } else { result.0 },
            },
        )?;
        let y = b.value(
            left.1,
            Op::Binary {
                op: Binary::Xor,
                left: if subtract { left.0 } else { right.0 },
                right: result.0,
            },
        )?;
        let signs = b.value(
            left.1,
            Op::Binary {
                op: Binary::And,
                left: x.0,
                right: y.0,
            },
        )?;
        let zero = b.value(left.1, Op::Constant(0))?;
        b.value(
            Type::I32,
            Op::Compare {
                op: Comparison::LessSigned,
                left: signs.0,
                right: zero.0,
            },
        )?
    };
    let zero = b.value(Type::I32, Op::Constant(0))?;
    let valid = b.value(
        Type::I32,
        Op::Compare {
            op: Comparison::Equal,
            left: overflow.0,
            right: zero.0,
        },
    )?;
    b.instructions.push(Instruction {
        result: None,
        op: Op::RequireRange(valid.0),
        il_offset: b.offset,
    });
    let result = if mixed {
        convert(b, result, if unsigned { 0x8b } else { 0xd4 })?
    } else {
        result
    };
    b.stack.push(result);
    Ok(())
}

pub(super) fn convert(
    b: &mut Builder<'_, '_>,
    mut value: (u32, Type),
    opcode: u16,
) -> Result<(u32, Type), String> {
    let (bits, signed, destination) = match opcode {
        0x82 | 0xb3 => (8, true, Type::I32),
        0x86 | 0xb4 => (8, false, Type::I32),
        0x83 | 0xb5 => (16, true, Type::I32),
        0x87 | 0xb6 => (16, false, Type::I32),
        0x84 | 0xb7 => (32, true, Type::I32),
        0x88 | 0xb8 => (32, false, Type::I32),
        0x85 | 0xb9 => (64, true, Type::I64),
        0x89 | 0xba => (64, false, Type::I64),
        0x8a | 0xd4 => (16, true, Type::Native),
        0x8b | 0xd5 => (16, false, Type::Native),
        _ => return Err("Invalid checked conversion opcode".into()),
    };
    let source_unsigned = (0x82..=0x8b).contains(&opcode);
    if matches!(value.1, Type::F32 | Type::F64) {
        let wide = value.1 == Type::F64;
        let raw = if wide { Type::I64 } else { Type::I32 };
        let raw_value = b.value(raw, Op::Reinterpret(value.0))?;
        let width = b.value(Type::I32, Op::Constant(bits.into()))?;
        let sign = b.value(Type::I32, Op::Constant(u64::from(signed)))?;
        let target = crate::contracts::float_range(
            b.resolver
                .ok_or("Checked float conversion requires runtime metadata")?,
            wide,
        )?;
        let valid = b.value(
            Type::I32,
            Op::Call {
                target,
                signature: Signature {
                    parameters: vec![raw, Type::I32, Type::I32],
                    returns: Some(Type::I32),
                },
                arguments: vec![raw_value.0, width.0, sign.0],
            },
        )?;
        b.instructions.push(Instruction {
            result: None,
            op: Op::RequireRange(valid.0),
            il_offset: b.offset,
        });
        let key = match (wide, bits == 64, signed) {
            (false, true, true) => "f32.toI64",
            (false, true, false) => "f32.toU64",
            (false, false, true) => "f32.toI32",
            (false, false, false) => "f32.toU32",
            (true, true, true) => "f64.toI64",
            (true, true, false) => "f64.toU64",
            (true, false, true) => "f64.toI32",
            (true, false, false) => "f64.toU32",
        };
        value = b.float_conversion(key, value)?;
        return b.value(
            destination,
            Op::Convert {
                value: value.0,
                bits,
                signed,
            },
        );
    }
    if !matches!(value.1, Type::I32 | Type::I64 | Type::Native) {
        return Err("Checked conversion requires an integer or floating operand".into());
    }
    let source_bits = value.1.bits() as u8;
    let mask = u64::MAX >> (64 - source_bits);
    let mut checks = Vec::new();
    if signed {
        if source_unsigned && bits <= source_bits {
            checks.push((Comparison::GreaterUnsigned, (1u64 << (bits - 1)) - 1));
        } else if !source_unsigned && bits < source_bits {
            checks.push((Comparison::LessSigned, (!((1u64 << (bits - 1)) - 1)) & mask));
            checks.push((Comparison::GreaterSigned, (1u64 << (bits - 1)) - 1));
        }
    } else {
        if !source_unsigned {
            checks.push((Comparison::LessSigned, 0));
        }
        if bits < source_bits {
            checks.push((Comparison::GreaterUnsigned, (1u64 << bits) - 1));
        }
    }
    let mut overflow = None;
    for (op, bound) in checks {
        let bound = b.value(value.1, Op::Constant(bound))?;
        let test = b.value(
            Type::I32,
            Op::Compare {
                op,
                left: value.0,
                right: bound.0,
            },
        )?;
        overflow = Some(if let Some(prior) = overflow {
            b.value(
                Type::I32,
                Op::Binary {
                    op: Binary::Or,
                    left: prior,
                    right: test.0,
                },
            )?
            .0
        } else {
            test.0
        });
    }
    if let Some(overflow) = overflow {
        let zero = b.value(Type::I32, Op::Constant(0))?;
        let valid = b.value(
            Type::I32,
            Op::Compare {
                op: Comparison::Equal,
                left: overflow,
                right: zero.0,
            },
        )?;
        b.instructions.push(Instruction {
            result: None,
            op: Op::RequireRange(valid.0),
            il_offset: b.offset,
        });
    }
    b.value(
        destination,
        Op::Convert {
            value: value.0,
            bits: bits.min(source_bits),
            signed: if bits > source_bits {
                !source_unsigned
            } else {
                signed
            },
        },
    )
}
