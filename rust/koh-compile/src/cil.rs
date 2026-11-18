#[derive(Debug, PartialEq)]
pub enum Operand {
    None,
    Integer(i64),
    Float32(u32),
    Float64(u64),
    Variable(u16),
    Token(u32),
    Target(u32),
    Targets(Vec<u32>),
}

#[derive(Debug, PartialEq)]
pub struct Instruction {
    pub offset: u32,
    pub opcode: u16,
    pub operand: Operand,
}

fn read<const N: usize>(bytes: &[u8], position: &mut usize) -> Result<[u8; N], String> {
    let end = position.checked_add(N).ok_or("CIL operand size overflow")?;
    let value = bytes.get(*position..end).ok_or("Truncated CIL operand")?;
    *position = end;
    value
        .try_into()
        .map_err(|_| "Invalid CIL operand width".into())
}

fn target(displacement: i32, end: usize, length: usize) -> Result<u32, String> {
    let value = end as i64 + i64::from(displacement);
    if value < 0 || value >= length as i64 {
        return Err(format!("Branch target {value} is outside the method"));
    }
    Ok(value as u32)
}

pub fn decode(bytes: &[u8]) -> Result<Vec<Instruction>, String> {
    if bytes.is_empty() {
        return Err("IL_0000: empty CIL method body".into());
    }
    if bytes.len() > 1024 * 1024 {
        return Err("IL_0000: CIL method exceeds 1 MiB limit".into());
    }
    let mut position = 0;
    let mut instructions = Vec::new();
    let mut boundaries = vec![false; bytes.len()];
    while position < bytes.len() {
        let offset = position;
        boundaries[offset] = true;
        let (opcode, operand) = (|| {
            let first = read::<1>(bytes, &mut position)?[0];
            let opcode = if first == 0xfe {
                0xfe00 | u16::from(read::<1>(bytes, &mut position)?[0])
            } else {
                u16::from(first)
            };
            let operand = match opcode {
                0x0e..=0x13 => Operand::Variable(u16::from(read::<1>(bytes, &mut position)?[0])),
                0xfe09..=0xfe0e => {
                    Operand::Variable(u16::from_le_bytes(read(bytes, &mut position)?))
                }
                0x1f => Operand::Integer(i64::from(read::<1>(bytes, &mut position)?[0] as i8)),
                0x20 => {
                    Operand::Integer(i64::from(i32::from_le_bytes(read(bytes, &mut position)?)))
                }
                0x21 => Operand::Integer(i64::from_le_bytes(read(bytes, &mut position)?)),
                0x22 => Operand::Float32(u32::from_le_bytes(read(bytes, &mut position)?)),
                0x23 => Operand::Float64(u64::from_le_bytes(read(bytes, &mut position)?)),
                0xfe12 | 0xfe19 => Operand::Integer(i64::from(read::<1>(bytes, &mut position)?[0])),
                0x27..=0x29
                | 0x6f..=0x75
                | 0x79
                | 0x7b..=0x81
                | 0x8c..=0x8d
                | 0x8f
                | 0xa3..=0xa5
                | 0xc2
                | 0xc6
                | 0xd0
                | 0xfe06..=0xfe07
                | 0xfe15..=0xfe16
                | 0xfe1c => Operand::Token(u32::from_le_bytes(read(bytes, &mut position)?)),
                0x2b..=0x37 | 0xde => {
                    let displacement = read::<1>(bytes, &mut position)?[0] as i8;
                    Operand::Target(target(i32::from(displacement), position, bytes.len())?)
                }
                0x38..=0x44 | 0xdd => {
                    let displacement = i32::from_le_bytes(read(bytes, &mut position)?);
                    Operand::Target(target(displacement, position, bytes.len())?)
                }
                0x45 => {
                    let count = u32::from_le_bytes(read(bytes, &mut position)?) as usize;
                    let end = count
                        .checked_mul(4)
                        .and_then(|length| position.checked_add(length))
                        .filter(|&end| end <= bytes.len())
                        .ok_or("Truncated or overflowing switch table")?;
                    let mut targets = Vec::with_capacity(count);
                    for _ in 0..count {
                        let displacement = i32::from_le_bytes(read(bytes, &mut position)?);
                        targets.push(target(displacement, end, bytes.len())?);
                    }
                    Operand::Targets(targets)
                }
                0x00..=0x0d
                | 0x14..=0x1e
                | 0x25..=0x26
                | 0x2a
                | 0x46..=0x6e
                | 0x76
                | 0x7a
                | 0x82..=0x8b
                | 0x8e
                | 0x90..=0xa2
                | 0xb3..=0xba
                | 0xc3
                | 0xd1..=0xdc
                | 0xdf..=0xe0
                | 0xfe00..=0xfe05
                | 0xfe0f
                | 0xfe11
                | 0xfe13..=0xfe14
                | 0xfe17..=0xfe18
                | 0xfe1a
                | 0xfe1d..=0xfe1e => Operand::None,
                _ => return Err(format!("Unknown or reserved CIL opcode 0x{opcode:04x}")),
            };
            Ok((opcode, operand))
        })()
        .map_err(|error: String| format!("IL_{offset:04x}: {error}"))?;
        instructions.push(Instruction {
            offset: offset as u32,
            opcode,
            operand,
        });
    }
    for instruction in &instructions {
        let targets = match &instruction.operand {
            Operand::Target(target) => std::slice::from_ref(target),
            Operand::Targets(targets) => targets.as_slice(),
            _ => continue,
        };
        for &target in targets {
            if boundaries.get(target as usize) != Some(&true) {
                return Err(format!(
                    "IL_{:04x}: target IL_{target:04x} is not an instruction boundary",
                    instruction.offset
                ));
            }
        }
    }
    Ok(instructions)
}

#[cfg(test)]
mod tests {
    use super::*;
    use rstest::rstest;

    #[rstest]
    #[case(&[0x1f, 0xff], 0x1f, Operand::Integer(-1))]
    #[case(&[0x20, 0x78, 0x56, 0x34, 0x12], 0x20, Operand::Integer(0x12345678))]
    #[case(&[0x20, 0, 0, 0, 0x80], 0x20, Operand::Integer(i32::MIN as i64))]
    #[case(&[0x21, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x7f], 0x21, Operand::Integer(i64::MAX))]
    #[case(&[0x22, 0, 0, 0x80, 0x3f], 0x22, Operand::Float32(0x3f800000))]
    #[case(&[0x22, 0, 0, 0, 0x80], 0x22, Operand::Float32(0x80000000))]
    #[case(&[0x22, 0x01, 0x23, 0xc1, 0x7f], 0x22, Operand::Float32(0x7fc12301))]
    #[case(&[0x23, 0, 0, 0, 0, 0, 0, 0xf0, 0x3f], 0x23, Operand::Float64(0x3ff0000000000000))]
    #[case(&[0x23, 0, 0, 0, 0, 0, 0, 0xf0, 0x7f], 0x23, Operand::Float64(0x7ff0000000000000))]
    #[case(&[0xfe, 0x19, 255], 0xfe19, Operand::Integer(255))]
    #[case(&[0x0e, 255], 0x0e, Operand::Variable(255))]
    #[case(&[0xfe, 0x09, 1, 2], 0xfe09, Operand::Variable(513))]
    #[case(&[0x28, 1, 0, 0, 6], 0x28, Operand::Token(0x06000001))]
    #[case(&[0xfe, 0x1c, 1, 0, 0, 2], 0xfe1c, Operand::Token(0x02000001))]
    fn preserves_operands(#[case] bytes: &[u8], #[case] opcode: u16, #[case] operand: Operand) {
        assert_eq!(
            decode(bytes).unwrap(),
            vec![Instruction {
                offset: 0,
                opcode,
                operand
            }]
        );
    }

    #[rstest]
    #[case(&[0x2b, 0xfe], vec![0])]
    #[case(&[0x38, 0xfb, 0xff, 0xff, 0xff], vec![0])]
    #[case(&[0x45, 1, 0, 0, 0, 0, 0, 0, 0, 0x2a], vec![9])]
    #[case(&[0x45, 2, 0, 0, 0, 0xf3, 0xff, 0xff, 0xff, 0, 0, 0, 0, 0x2a], vec![0, 13])]
    fn resolves_relative_targets(#[case] bytes: &[u8], #[case] expected: Vec<u32>) {
        let decoded = decode(bytes).unwrap();
        let actual = match &decoded[0].operand {
            Operand::Target(target) => vec![*target],
            Operand::Targets(targets) => targets.clone(),
            operand => panic!("Unexpected operand {operand:?}"),
        };
        assert_eq!(actual, expected);
    }

    #[rstest]
    #[case(&[], "empty")]
    #[case(&[0x24], "opcode")]
    #[case(&[0xfe, 0xff], "opcode")]
    #[case(&[0xfe], "Truncated")]
    #[case(&[0x20, 1], "Truncated")]
    #[case(&[0x45, 0xff, 0xff, 0xff, 0xff], "switch")]
    #[case(&[0x2b, 0xff], "boundary")]
    #[case(&[0x2b, 0], "outside")]
    #[case(&[0x2b, 0x80], "outside")]
    #[case(&[0x45, 1, 0, 0, 0], "switch")]
    #[case(&[0x45, 1, 0, 0, 0, 0xf8, 0xff, 0xff, 0xff, 0x2a], "boundary")]
    #[case(&[0x2b, 1, 0xfe, 1, 0x2a], "boundary")]
    fn diagnoses_malformed_il(#[case] bytes: &[u8], #[case] diagnostic: &str) {
        let error = decode(bytes).unwrap_err();
        assert!(error.contains(diagnostic), "{error}");
        assert!(error.contains("IL_0000"), "{error}");
    }

    #[test]
    fn offsets_are_original_il_byte_offsets() {
        let decoded = decode(&[0x0e, 7, 0xfe, 1, 0x2a]).unwrap();
        assert_eq!(
            decoded.iter().map(|i| i.offset).collect::<Vec<_>>(),
            vec![0, 2, 4]
        );
    }

    #[test]
    fn diagnoses_the_actual_failing_offset() {
        assert!(decode(&[0, 0x20, 1]).unwrap_err().starts_with("IL_0001:"));
    }

    #[test]
    fn bounds_method_allocations() {
        let mut bytes = vec![0; 1024 * 1024];
        assert_eq!(decode(&bytes).unwrap().len(), bytes.len());
        bytes.push(0);
        assert!(decode(&bytes).unwrap_err().contains("1 MiB"));
    }
}
