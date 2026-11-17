pub const SHAPES: &[&str] = &[
    "Adjacent",
    "AcrossArithmetic",
    "Different",
    "InterveningRead",
    "AliasWrite",
    "Hram",
    "Vram",
    "Join",
    "ReloadAfterAlu",
    "ReadPair",
    "Call",
    "Tail",
    "NotTail",
    "ReturnJoin",
    "PostIncrement",
    "IncrementEntry",
    "PostDecrement",
    "BranchStores",
];
pub const INPUTS: &[(u32, u32)] = &[
    (0, 0),
    (1, 2),
    (0x7f, 0x80),
    (0xff, 0x12),
    (0x1234, 0x56),
    (u32::MAX, 0xff),
    (0x80000000, 0x1234),
    (2, u32::MAX),
];
pub const WIDE: &[(u64, u64)] = &[
    (0, 0),
    (0, 1),
    (0xffff, 1),
    (0xffffffff, 1),
    (u64::MAX, 1),
    (0x00ff00ff00ff00ff, 1),
    (0x8000000000000000, 1),
    (0x8000000000000001, 7),
    (1, 8),
    (0x123456789abcdef0, 31),
    (u64::MAX, 32),
    (0x8000000000000001, 63),
];

pub fn expected(shape: &str, x: u32, y: u32) -> (u64, String) {
    let a = x as u8;
    let b = y as u8;
    let w = |address: u16, value: u8| format!("W{address:04X}:{value:02X}");
    let r = |address: u16| format!("R{address:04X}");
    let both = |a: u8, b: u8| u32::from(a) * 256 + u32::from(b);
    let (value, events) = match shape {
        "Adjacent" | "Call" => (u32::from(b), vec![w(0xc020, a), w(0xc020, b), r(0xc020)]),
        "AcrossArithmetic" => (
            u32::from(b.wrapping_add(1)),
            vec![w(0xc020, a), w(0xc020, b.wrapping_add(1)), r(0xc020)],
        ),
        "Different" | "PostIncrement" => (
            both(a, b),
            vec![w(0xc020, a), w(0xc021, b), r(0xc020), r(0xc021)],
        ),
        "InterveningRead" => (
            both(a, b),
            vec![w(0xc020, a), r(0xc020), w(0xc020, b), r(0xc020)],
        ),
        "AliasWrite" => (
            if x & 1 == 0 {
                both(b, 0x22)
            } else {
                both(0x11, b)
            },
            vec![
                w(0xc020, 0x11),
                w(0xc021, 0x22),
                w(if x & 1 == 0 { 0xc020 } else { 0xc021 }, b),
                r(0xc020),
                r(0xc021),
            ],
        ),
        "Hram" | "Vram" => {
            let address = if shape == "Hram" { 0xff80 } else { 0x9800 };
            (u32::from(b), vec![w(address, a), w(address, b), r(address)])
        }
        "Join" => {
            let mut e = vec![w(0xc020, 0x11)];
            if x != 0 {
                e.push(w(0xc020, b));
            }
            e.push(r(0xc020));
            (u32::from(if x != 0 { b } else { 0x11 }), e)
        }
        "ReloadAfterAlu" => (
            (u32::from(a) << 16) | (u32::from(a).wrapping_add(y) & 0xffff),
            vec![w(0xc020, a), r(0xc020), r(0xc020)],
        ),
        "ReadPair" => (x, vec![r(0xff44), r(0xff44)]),
        "Tail" => (x.wrapping_add(3), vec![]),
        "NotTail" => (x.wrapping_add(3).wrapping_add(y), vec![]),
        "ReturnJoin" => (
            if x == 0 {
                y.wrapping_add(3)
            } else {
                x.wrapping_add(6)
            },
            vec![],
        ),
        "IncrementEntry" => {
            let mut e = vec![w(0xc020, a), w(0xc021, b)];
            if x & 1 != 0 {
                e.push(r(0xc020));
            }
            e.push(r(0xc021));
            (both(if x & 1 != 0 { a } else { 0 }, b), e)
        }
        "PostDecrement" => (
            u32::from(a) | 0x6600,
            vec![
                w(0xc020, a),
                w(0xc021, b),
                w(0xc021, 0x66),
                r(0xc020),
                r(0xc021),
            ],
        ),
        "BranchStores" => {
            let value = if x == 0 { b } else { b.wrapping_add(1) };
            (u32::from(value), vec![w(0xc020, value), r(0xc020)])
        }
        _ => panic!("Unknown rewrite contract"),
    };
    (
        u64::from(value),
        if events.is_empty() {
            "-".into()
        } else {
            events.join(",")
        },
    )
}
