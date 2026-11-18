pub const INPUTS: &[(i32, i32, i32)] = &[
    (-1, 0, 1),
    (0, 0, 1),
    (0, i32::MAX, 0),
    (1, 0, 0),
    (1, 0, 1),
    (1, 1, 1),
    (2, -1, 1),
    (3, 200, 1),
    (7, 0x12345678, 1),
    (3, i32::MAX, 1),
    (2, i32::MIN, 1),
    (4, 17, 0),
    (4, -7, -1),
    (4, 0, -1),
];

pub fn expected(shape: &str, n: i32, seed: i32, flag: i32) -> Result<u64, u8> {
    let mut sum = 0i32;
    let mut iterations = 0u32;
    let mut cell = seed;
    let count = n.max(0) as u32;
    let count = if shape == "Invariant" {
        count * 2
    } else {
        count
    };
    if flag != 0 || shape == "Nested" {
        for i in 0..count {
            let i = i as i32;
            match shape {
                "Invariant" => sum = sum.wrapping_add(seed.wrapping_mul(2)),
                "Variant" => sum = sum.wrapping_add(seed.wrapping_add(i)),
                "Loaded" => {
                    sum = sum.wrapping_add(cell);
                    cell = cell.wrapping_add(3);
                }
                "Nested" => {
                    let v = seed.wrapping_add(i);
                    let repeated = if flag != 0 { v.wrapping_mul(3) } else { 0 };
                    sum = sum
                        .wrapping_add(repeated)
                        .wrapping_add(v)
                        .wrapping_add(seed);
                }
                "Division" => {
                    if seed == 0 {
                        return Err(2);
                    }
                    sum = sum.wrapping_add(100 / seed);
                }
                _ => panic!("Unknown loop contract"),
            }
            iterations += 1;
        }
    }
    if shape == "Loaded" {
        sum = sum.wrapping_add(cell);
    }
    Ok((u64::from(iterations) << 32) | u64::from(sum as u32))
}
