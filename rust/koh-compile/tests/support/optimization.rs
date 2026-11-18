pub const SHAPES: &[&str] = &[
    "Identities",
    "DeadChain",
    "WriteOnly",
    "Add",
    "ByteAdd",
    "SignedDivide",
    "UnsignedDivide",
    "SignedCompare",
    "UnsignedCompare",
    "Convert",
    "Cascade",
    "Duplicate",
    "MultiplyPower",
    "UnsignedPower",
    "UnsignedRemainder",
    "MultiplyOther",
    "SignedPower",
    "NegativePower",
    "Join",
    "FoldedJoin",
    "ScalarLocal",
    "RepeatedLoads",
    "Escaped",
    "Distinct",
    "IndexedAlias",
    "UnusedBusRead",
    "UnusedCheckedLoad",
    "InlineLeaf",
];

pub const INPUTS: &[(u32, u32)] = &[
    (20, 22),
    (200, 100),
    (0xf8, 2),
    (0xffff, 0),
    (0, 0),
    (0xff, 0),
    (0x1234, 200),
    (200, 0),
    (0x80, u32::MAX),
    (0x80000000, u32::MAX),
    (u32::MAX, 0x7fffffff),
    (1, 2),
];

pub fn expected(shape: &str, x: u32, y: u32) -> Result<u64, u8> {
    let byte = x as u8;
    let signed = i32::from(byte as i8);
    let value = match shape {
        "Add" | "Escaped" => x.wrapping_add(y),
        "ByteAdd" => u32::from(byte.wrapping_add(y as u8)),
        "SignedDivide" => {
            let divisor = i32::from(y as u8 as i8);
            if divisor == 0 {
                return Err(2);
            }
            (signed / divisor) as u32
        }
        "UnsignedDivide" => {
            if y as u8 == 0 {
                return Err(2);
            }
            u32::from(byte / y as u8)
        }
        "SignedCompare" => u32::from(signed < i32::from(y as u8 as i8)),
        "UnsignedCompare" => u32::from(byte < y as u8),
        "Convert" => {
            return Ok(u64::from(byte)
                | (u64::from(y as u8) << 16)
                | (u64::from(y as u8 as i8 as i32 as u32) << 32));
        }
        "Identities" => return Ok(u64::from(byte) * (1 | (1 << 16) | (1 << 32) | (1 << 40))),
        "DeadChain" | "WriteOnly" => x,
        "Cascade" => x.wrapping_add(y).wrapping_mul(10),
        "Duplicate" => x.wrapping_add(y).wrapping_mul(2),
        "MultiplyPower" => u32::from((x as u16).wrapping_mul(8)),
        "UnsignedPower" => u32::from(x as u16 / 4),
        "UnsignedRemainder" => u32::from(byte % 8),
        "MultiplyOther" => u32::from(byte.wrapping_mul(3)),
        "SignedPower" => (signed / 4) as u32,
        "NegativePower" => (signed / -128) as u32,
        "Join" => {
            if byte > 10 {
                1
            } else {
                2
            }
        }
        "FoldedJoin" => {
            if x != 0 {
                5
            } else {
                7
            }
        }
        "ScalarLocal" => u32::from(byte.wrapping_mul(2)),
        "RepeatedLoads" => x.wrapping_mul(2),
        "Distinct" => x.wrapping_add(1).wrapping_add(y),
        "IndexedAlias" => 0x44002211 | (u32::from(y as u8) << 16),
        "UnusedBusRead" => y,
        "UnusedCheckedLoad" => return Err(4),
        "InlineLeaf" => x.wrapping_add(3),
        _ => panic!("Unknown optimization contract"),
    };
    Ok(u64::from(value))
}
