pub const SHAPES: &[&str] = &[
    "FlowCopy",
    "FlowCall",
    "FlowFill",
    "FlowFillExit",
    "FlowOverlap",
    "FlowCounters",
    "FlowCounterChoice",
    "FlowFills",
    "FlowCopyChoice",
    "FlowCopies",
    "FlowCopyFill",
    "LoopExit",
    "LoopCall",
    "LoopNested",
    "LoopBreak",
    "LoopOld",
    "LoopLast",
    "GuardedCopy",
    "SequentialCopy",
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
    "IndexedStore",
    "IndexedLoad",
    "IndexedTwice",
    "IndexedDelayed",
    "IndexedReuse",
    "WalkChoice",
    "WalkLinear",
    "WalkStore",
];
pub const FLOW_INPUTS: &[(u32, u32)] = &[
    (0, 0),
    (1, 1),
    (10, 10),
    (10, 20),
    (16, 20),
    (20, 10),
    (32, 32),
];
pub const FLOW_CHOICE_INPUTS: &[(u32, u32)] = &[
    (0, 0),
    (0, 1),
    (1, 0),
    (1, 1),
    (10, 0),
    (10, 1),
    (16, 0),
    (16, 1),
];
pub const FLOW_OVERLAP_INPUTS: &[(u32, u32)] = &[(0, 0), (1, 0), (4, 2), (4, 1), (8, 2), (16, 4)];
pub const LOOP_INPUTS: &[(u32, u32)] = &[(0, 0), (1, 0), (10, 10), (50, 5), (100, 42), (16, 16)];
pub const NESTED_INPUTS: &[(u32, u32)] = &[(0, 10), (10, 0), (1, 1), (10, 10), (16, 16), (17, 17)];
pub const GUARD_INPUTS: &[(u32, u32)] = &[
    (148, 8),
    (200, 8),
    (143, 8),
    (144, 8),
    (152, 8),
    (153, 8),
    (0, 0),
    (148, 0),
    (148, 1),
    (255, 1),
];
pub const SEQUENTIAL_INPUTS: &[(u32, u32)] = &[(148, 148), (148, 200), (200, 148), (200, 200)];
pub const WALK_INPUTS: &[(u32, u32)] = &[
    (0, 0),
    (1, 0),
    (0, 4),
    (1, 4),
    (0, 8),
    (1, 8),
    (0, 20),
    (1, 20),
];
pub const INDEX_INPUTS: &[(u32, u32)] = &[
    (0xc050, 3),
    (0xc050, 4),
    (0xc060, 1),
    (0xc070, 2),
    (0xc0f8, 30),
    (0xc050, u32::MAX),
    (0xc180, 0xfffffff0),
    (0xc050, 0x101),
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
    if shape.starts_with("Flow") {
        let mut events = Vec::new();
        let seed = |events: &mut Vec<String>, address, count, base: u8| {
            for i in 0..count {
                events.push(w(address + i, base.wrapping_add(i as u8)));
            }
        };
        let copy =
            |events: &mut Vec<String>, source, destination, count, base: u8, increment: u8| {
                for i in 0..count {
                    events.extend([
                        r(source + i),
                        w(
                            destination + i,
                            base.wrapping_add(i as u8).wrapping_add(increment),
                        ),
                    ]);
                }
            };
        let fill = |events: &mut Vec<String>, address, count, byte| {
            for i in 0..count {
                events.push(w(address + i, byte));
            }
        };
        let pair = |first: u16, second: u16| (u64::from(first) << 32) | u64::from(second);
        let n = x as u16;
        let m = y as u16;
        let value = match shape {
            "FlowCopy" | "FlowCall" => {
                if shape == "FlowCopy" {
                    events.push(w(0xc100, 0));
                }
                seed(&mut events, 0xc100, n, 0);
                copy(
                    &mut events,
                    0xc100,
                    0xc200,
                    n,
                    0,
                    u8::from(shape == "FlowCall"),
                );
                pair(0xc100 + n, 0xc200 + n)
            }
            "FlowFill" | "FlowFillExit" => {
                fill(&mut events, 0xc100, n, 0xaa);
                if shape == "FlowFillExit" {
                    events.push(w(0xc100 + n, 0xff));
                }
                u64::from(0xc100 + n)
            }
            "FlowOverlap" => {
                let mut memory = vec![0u8; 40];
                for i in 0..20u16 {
                    memory[i as usize] = 10 * (i as u8 + 1);
                    events.push(w(0xc100 + i, memory[i as usize]));
                }
                for i in 0..n {
                    let byte = memory[i as usize];
                    memory[(m + i) as usize] = byte;
                    events.extend([r(0xc100 + i), w(0xc100 + m + i, byte)]);
                }
                pair(0xc100 + n, 0xc100 + m + n)
            }
            "FlowCounters" => {
                events.extend([w(0xc100, a), w(0xc200, b)]);
                (u64::from(a) << 16) | u64::from(b)
            }
            "FlowCounterChoice" => {
                let total = if y != 0 { a } else { a.wrapping_mul(2) };
                events.extend([w(0xc020, b), r(0xc020), w(0xc300, total)]);
                u64::from(total)
            }
            "FlowFills" => {
                fill(&mut events, 0xc100, n, 0x11);
                fill(&mut events, 0xc200, m, 0x22);
                pair(0xc100 + n, 0xc200 + m)
            }
            "FlowCopyChoice" => {
                events.extend([w(0xc020, b), r(0xc020)]);
                let (source, base) = if y != 0 { (0xc100, 0x80) } else { (0xc200, 1) };
                seed(&mut events, source, n, base);
                copy(&mut events, source, source + 16, n, base, 0);
                pair(source + n, source + 16 + n)
            }
            "FlowCopies" => {
                seed(&mut events, 0xc100, n, 0);
                copy(&mut events, 0xc100, 0xc200, n, 0, 0);
                seed(&mut events, 0xc300, m, 100);
                copy(&mut events, 0xc300, 0xc400, m, 100, 0);
                pair(0xc200 + n, 0xc400 + m)
            }
            "FlowCopyFill" => {
                seed(&mut events, 0xc100, n, 0);
                copy(&mut events, 0xc100, 0xc200, n, 0, 0);
                fill(&mut events, 0xc300, m, 0x33);
                pair(0xc200 + n, 0xc300 + m)
            }
            _ => panic!("Unknown flow contract"),
        };
        return (
            value,
            if events.is_empty() {
                "-".into()
            } else {
                events.join(",")
            },
        );
    }
    if shape.starts_with("Loop") {
        let count = x as u8;
        let (value, events) = match shape {
            "LoopExit" => (u64::from(count), vec![w(0xc100, count)]),
            "LoopCall" => {
                let touched = count.wrapping_add(5);
                (
                    (u64::from(count) << 16) | u64::from(touched),
                    vec![w(0xc100, touched)],
                )
            }
            "LoopNested" => {
                let total = x.wrapping_mul(y) as u8;
                (u64::from(total), vec![w(0xc100, total)])
            }
            "LoopBreak" => {
                let end = if y < x { y as u8 } else { count };
                (u64::from(end), vec![w(0xc100, end)])
            }
            "LoopOld" => (u64::from(count), (0..count).map(|i| w(0xc100, i)).collect()),
            "LoopLast" => {
                let last = count.saturating_sub(1);
                (
                    (u64::from(count) << 16) | u64::from(last),
                    vec![w(0xc101, last)],
                )
            }
            _ => panic!("Unknown loop shape"),
        };
        return (
            value,
            if events.is_empty() {
                "-".into()
            } else {
                events.join(",")
            },
        );
    }
    if shape == "SequentialCopy" {
        let mut events = Vec::new();
        for i in 0..6 {
            events.extend([w(0xc100 + i, 0x10 + i as u8), w(0xc140 + i, 0x80 + i as u8)]);
        }
        let mut value = 0;
        for (gate, src, dst, byte, shift) in
            [(x, 0xc100, 0xc200, 0x10, 0), (y, 0xc140, 0xc220, 0x80, 16)]
        {
            if (144..=152).contains(&gate) {
                value |= 6u64 << shift;
                for i in 0..6 {
                    events.extend([r(src + i), w(dst + i, byte + i as u8)]);
                }
            }
        }
        return (value, events.join(","));
    }
    if shape == "GuardedCopy" {
        let mut events = Vec::new();
        for i in 0..8 {
            events.extend([w(0xc100 + i, 0x40 + i as u8), w(0xc200 + i, 0xee)]);
        }
        let copied = if (144..=152).contains(&(x as u8)) {
            y
        } else {
            0
        };
        for i in 0..copied as u16 {
            events.extend([r(0xc100 + i), w(0xc200 + i, 0x40 + i as u8)]);
        }
        return (u64::from(copied), events.join(","));
    }
    if shape.starts_with("Walk") {
        let first = if shape == "WalkLinear" { 1 } else { 0x10 };
        let start = if shape == "WalkLinear" || x != 0 {
            0xc100
        } else {
            0xc200
        };
        let mut events = Vec::new();
        for i in 0..20 {
            events.push(w(0xc100 + i, first + i as u8));
            events.push(w(0xc200 + i, 0x40 + i as u8));
        }
        let value = if shape == "WalkStore" {
            for i in 0..y as u16 {
                events.push(w(start + i, 0xaa));
            }
            events.extend([r(0xc100), r(0xc200)]);
            let a: u8 = if start == 0xc100 && y != 0 {
                0xaa
            } else {
                0x10
            };
            let b: u8 = if start == 0xc200 && y != 0 {
                0xaa
            } else {
                0x40
            };
            u64::from(a) | (u64::from(b) << 16)
        } else {
            events.push(w(0xc300, 0));
            let mut last = 0;
            for i in 0..y as u16 {
                last = if start == 0xc100 { first } else { 0x40 } + i as u8;
                events.extend([r(start + i), w(0xc300, last)]);
            }
            events.push(r(0xc300));
            u64::from(last)
        };
        return (
            (u64::from(start + y as u16) << 32) | value,
            events.join(","),
        );
    }
    if shape.starts_with("Indexed") {
        let address = (x as u16).wrapping_add(y as u16);
        let (value, events) = match shape {
            "IndexedStore" => (77, vec![w(address, 77), r(address)]),
            "IndexedLoad" => (200, vec![w(address, 200), r(address)]),
            "IndexedTwice" => (10, vec![w(address, 9), r(address)]),
            "IndexedDelayed" => (
                (u64::from((x ^ y).wrapping_add(3)) << 32) | 42,
                vec![w(address, 41), r(address), w(address, 42), r(address)],
            ),
            "IndexedReuse" => (2, vec![w(address, 1), w(address, 2), r(address)]),
            _ => panic!("Unknown indexed shape {shape}"),
        };
        return (value, events.join(","));
    }
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
