use clrmeta::{CallingConvention, ElementType, MethodSig, TypeSig};

#[derive(Clone, Copy)]
enum Position {
    Value,
    Field,
    Return,
    Parameter,
    Local,
    Pointer,
}

struct Parser<'a> {
    bytes: &'a [u8],
    position: usize,
}

fn parse<T>(
    bytes: &[u8],
    read: impl FnOnce(&mut Parser<'_>) -> Result<T, String>,
) -> Result<T, String> {
    if bytes.len() > 65536 {
        return Err("signature byte 0: exceeds 64 KiB limit".into());
    }
    let mut parser = Parser { bytes, position: 0 };
    let result = read(&mut parser).and_then(|value| {
        if parser.position != bytes.len() {
            return Err("Trailing signature bytes".into());
        }
        Ok(value)
    });
    result.map_err(|error| format!("signature byte {}: {error}", parser.position))
}

pub fn method(bytes: &[u8]) -> Result<MethodSig, String> {
    parse(bytes, |parser| parser.method(0))
}

pub fn method_definition(bytes: &[u8]) -> Result<MethodSig, String> {
    let signature = method(bytes)?;
    let convention = signature.calling_convention.0;
    if !matches!(convention & 15, 0 | 5)
        || convention & 0x40 != 0
        || signature.sentinel.is_some()
        || (convention & 15 == 5 && signature.generic_param_count != 0)
    {
        return Err("signature byte 0: invalid MethodDef calling convention or sentinel".into());
    }
    Ok(signature)
}

pub fn field(bytes: &[u8]) -> Result<TypeSig, String> {
    parse(bytes, |parser| {
        parser.expect(6)?;
        parser.ty(Position::Field, 0)
    })
}

pub fn locals(bytes: &[u8]) -> Result<Vec<TypeSig>, String> {
    parse(bytes, |parser| {
        parser.expect(7)?;
        let count = parser.unsigned()?;
        parser.sequence(count, Position::Local, 0)
    })
}

pub fn type_spec(bytes: &[u8]) -> Result<TypeSig, String> {
    parse(bytes, |parser| parser.ty(Position::Value, 0))
}

pub fn method_spec(bytes: &[u8]) -> Result<Vec<TypeSig>, String> {
    parse(bytes, |parser| {
        parser.expect(0x0a)?;
        let count = parser.unsigned()?;
        if count == 0 {
            return Err("Generic instantiation needs type arguments".into());
        }
        parser.sequence(count, Position::Value, 0)
    })
}

impl Parser<'_> {
    fn byte(&mut self) -> Result<u8, String> {
        let value = *self.bytes.get(self.position).ok_or("Truncated signature")?;
        self.position += 1;
        Ok(value)
    }

    fn expect(&mut self, expected: u8) -> Result<(), String> {
        let actual = self.byte()?;
        if actual != expected {
            return Err(format!(
                "Expected signature marker {expected:02x}, got {actual:02x}"
            ));
        }
        Ok(())
    }

    fn compressed(&mut self) -> Result<(u32, u32), String> {
        let first = self.byte()?;
        match first {
            0..=0x7f => Ok((u32::from(first), 7)),
            0x80..=0xbf => Ok(((u32::from(first & 0x3f) << 8) | u32::from(self.byte()?), 14)),
            0xc0..=0xdf => {
                let mut value = u32::from(first & 0x1f);
                for _ in 0..3 {
                    value = (value << 8) | u32::from(self.byte()?);
                }
                Ok((value, 29))
            }
            _ => Err("Invalid compressed integer prefix".into()),
        }
    }

    fn unsigned(&mut self) -> Result<u32, String> {
        let (value, bits) = self.compressed()?;
        if (bits == 14 && value < 128) || (bits == 29 && value < 16384) {
            return Err("Noncanonical compressed unsigned integer".into());
        }
        Ok(value)
    }

    fn signed(&mut self) -> Result<i32, String> {
        let (value, bits) = self.compressed()?;
        let sign = if value & 1 != 0 { !0 << (bits - 1) } else { 0 };
        let result = (value >> 1) as i32 | sign;
        if (bits == 14 && (-64..=63).contains(&result))
            || (bits == 29 && (-8192..=8191).contains(&result))
        {
            return Err("Noncanonical compressed signed integer".into());
        }
        Ok(result)
    }

    fn type_token(&mut self) -> Result<u32, String> {
        let token = self.unsigned()?;
        if token >> 2 == 0 || token & 3 == 3 || token >> 2 > 0x00ff_ffff {
            return Err("Invalid TypeDefOrRefOrSpec encoded token".into());
        }
        Ok(token)
    }

    fn bounded_count(&self, count: u32) -> Result<usize, String> {
        if count as usize > self.bytes.len() - self.position {
            return Err("Signature count exceeds remaining input".into());
        }
        Ok(count as usize)
    }

    fn sequence(
        &mut self,
        count: u32,
        position: Position,
        depth: usize,
    ) -> Result<Vec<TypeSig>, String> {
        let count = self.bounded_count(count)?;
        let mut types = Vec::with_capacity(count);
        for _ in 0..count {
            types.push(self.ty(position, depth)?);
        }
        Ok(types)
    }

    fn method(&mut self, depth: usize) -> Result<MethodSig, String> {
        let flags = self.byte()?;
        if flags & 0x80 != 0
            || !matches!(flags & 15, 0..=5 | 9)
            || flags & 0x40 != 0 && flags & 0x20 == 0
        {
            return Err("Invalid method calling convention".into());
        }
        let generic_param_count = if flags & 0x10 != 0 {
            let count = self.unsigned()?;
            if count == 0 {
                return Err("Generic method needs nonzero arity".into());
            }
            count
        } else {
            0
        };
        let count = self.unsigned()?;
        let return_type = self.ty(Position::Return, depth)?;
        let count = self.bounded_count(count)?;
        let mut params = Vec::with_capacity(count);
        let mut sentinel = None;
        for index in 0..count {
            if self.bytes.get(self.position) == Some(&0x41) {
                if flags & 15 != 5 || sentinel.is_some() {
                    return Err("Invalid vararg sentinel".into());
                }
                self.byte()?;
                sentinel = Some(index);
            }
            params.push(self.ty(Position::Parameter, depth)?);
        }
        Ok(MethodSig {
            calling_convention: CallingConvention(flags),
            generic_param_count,
            return_type,
            params,
            sentinel,
        })
    }

    fn ty(&mut self, position: Position, depth: usize) -> Result<TypeSig, String> {
        if depth >= 64 {
            return Err("Type nesting exceeds 64 levels".into());
        }
        let element = self.byte()?;
        Ok(match element {
            1 if matches!(position, Position::Return | Position::Pointer) => {
                TypeSig::Primitive(ElementType::Void)
            }
            2..=0x0e | 0x18..=0x19 | 0x1c => {
                TypeSig::Primitive(ElementType::from_u8(element).ok_or("Invalid primitive type")?)
            }
            0x16 if matches!(
                position,
                Position::Return | Position::Parameter | Position::Local
            ) =>
            {
                TypeSig::Primitive(ElementType::TypedByRef)
            }
            0x11 => TypeSig::ValueType(self.type_token()?),
            0x12 => TypeSig::Class(self.type_token()?),
            0x0f => TypeSig::Ptr(Box::new(self.ty(Position::Pointer, depth + 1)?)),
            0x10 if matches!(
                position,
                Position::Return | Position::Parameter | Position::Local | Position::Field
            ) =>
            {
                TypeSig::ByRef(Box::new(self.ty(Position::Value, depth + 1)?))
            }
            0x13 => TypeSig::Var(self.unsigned()?),
            0x1e => TypeSig::MVar(self.unsigned()?),
            0x1d => TypeSig::SzArray(Box::new(self.ty(Position::Value, depth + 1)?)),
            0x14 => {
                let element_type = Box::new(self.ty(Position::Value, depth + 1)?);
                let rank = self.unsigned()?;
                if rank == 0 {
                    return Err("Array rank must be positive".into());
                }
                let size_count = self.unsigned()?;
                if size_count > rank {
                    return Err("Array sizes exceed rank".into());
                }
                let mut sizes = Vec::with_capacity(self.bounded_count(size_count)?);
                for _ in 0..size_count {
                    sizes.push(self.unsigned()?);
                }
                let bound_count = self.unsigned()?;
                if bound_count > rank {
                    return Err("Array lower bounds exceed rank".into());
                }
                let mut lo_bounds = Vec::with_capacity(self.bounded_count(bound_count)?);
                for _ in 0..bound_count {
                    lo_bounds.push(self.signed()?);
                }
                TypeSig::Array {
                    element_type,
                    rank,
                    sizes,
                    lo_bounds,
                }
            }
            0x15 => {
                let kind = self.byte()?;
                if !matches!(kind, 0x11..=0x12) {
                    return Err("Generic instance needs class or value type".into());
                }
                let type_ref = self.type_token()?;
                let count = self.unsigned()?;
                if count == 0 {
                    return Err("Generic instantiation needs type arguments".into());
                }
                let type_args = self.sequence(count, Position::Value, depth + 1)?;
                TypeSig::GenericInst {
                    is_value_type: kind == 0x11,
                    type_ref,
                    type_args,
                }
            }
            0x1b => TypeSig::FnPtr(Box::new(self.method(depth + 1)?)),
            0x1f..=0x20 => {
                let modifier = self.type_token()?;
                let inner = Box::new(self.ty(position, depth + 1)?);
                TypeSig::Modified {
                    required: element == 0x1f,
                    modifier,
                    inner,
                }
            }
            0x45 if matches!(position, Position::Local) => {
                TypeSig::Pinned(Box::new(self.ty(Position::Parameter, depth + 1)?))
            }
            _ => {
                return Err(format!(
                    "Invalid element type 0x{element:02x} in this signature position"
                ));
            }
        })
    }
}
