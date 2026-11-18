fn sprites(completed: usize) -> [u8; 16] {
    let dx = [
        24i16, 22, 17, 9, 0, -9, -17, -22, -24, -22, -17, -9, 0, 9, 17, 22,
    ];
    let dy = [
        0i16, 9, 17, 22, 24, 22, 17, 9, 0, -9, -17, -22, -24, -22, -17, -9,
    ];
    let mut result = [0; 16];
    for slot in 0..4 {
        let phase = (completed - 1 + slot * 4) % 16;
        result[slot * 4..slot * 4 + 4].copy_from_slice(&[
            (88 + dy[phase]) as u8,
            (88 + dx[phase]) as u8,
            2,
            (slot * 32) as u8,
        ]);
    }
    result
}

pub(super) fn assert_timeline(bytes: &[u8], color: bool) {
    assert_eq!(&sprites(1)[..4], &[88, 112, 2, 0]);
    assert_eq!(&sprites(5)[..4], &[112, 88, 2, 0]);
    assert_eq!(bytes.len() % 39, 0);
    let mut observed = Vec::new();
    for record in bytes.chunks_exact(39) {
        let digits = &record[..5];
        let Some(first) = digits.iter().position(|&digit| digit != 16) else {
            continue;
        };
        let Some(score) = digits[first..].iter().try_fold(0usize, |value, &digit| {
            (32..=41)
                .contains(&digit)
                .then(|| value * 10 + usize::from(digit) - 32)
        }) else {
            continue;
        };
        if score == 0 || score % 17 != 0 {
            continue;
        }
        let completed = score / 17;
        if completed > 80 {
            continue;
        }
        let alt = (completed - 1) / 60 % 2 != 0;
        if record[5..21] != sprites(completed) {
            continue;
        }
        if color {
            let bg: [u8; 8] = if alt {
                [255, 127, 128, 102, 0, 57, 0, 0]
            } else {
                [255, 127, 52, 83, 200, 33, 0, 0]
            };
            let obj: [u8; 8] = if alt {
                [0, 0, 255, 3, 0, 124, 0, 0]
            } else {
                [0, 0, 255, 127, 31, 0, 0, 0]
            };
            if record[23..31] != bg || record[31..39] != obj {
                continue;
            }
        } else if record[21..23] != if alt { [0x93, 0xe0] } else { [0xe4, 0x1c] } {
            continue;
        }
        observed.push(completed);
    }
    observed.dedup();
    let first = *observed
        .first()
        .expect("no coherent HUD/OAM/palette observation");
    assert!(
        (2..=10).contains(&first),
        "bounded first painted iteration: {observed:?}"
    );
    let expected: Vec<_> = (first..=79).collect();
    assert!(
        observed.starts_with(&expected),
        "every completed phase through the theme boundary must appear in order: {observed:?}"
    );
    assert_ne!(&sprites(first)[..4], &sprites(first + 11)[..4]);
}
