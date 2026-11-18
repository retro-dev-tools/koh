use crate::engine::Memory;

pub fn parse_address(text: &str) -> Option<u16> {
    if text.is_empty() || text.len() > 4 || !text.bytes().all(|b| b.is_ascii_hexdigit()) {
        return None;
    }
    u16::from_str_radix(text, 16).ok()
}

pub fn navigate_memory(address: u16, key: &str) -> Option<u16> {
    Some(match key {
        "pageup" => address.wrapping_sub(256),
        "pagedown" => address.wrapping_add(256),
        "home" => 0,
        _ => return None,
    })
}

pub fn memory_rows(memory: &Memory) -> Vec<String> {
    memory
        .bytes
        .chunks(16)
        .enumerate()
        .map(|(row, bytes)| {
            let address = memory.base_address.wrapping_add((row * 16) as u16);
            let hex = bytes
                .iter()
                .map(|byte| format!("{byte:02X}"))
                .collect::<Vec<_>>()
                .join(" ");
            let ascii = bytes
                .iter()
                .map(|byte| {
                    if (32..=126).contains(byte) {
                        char::from(*byte)
                    } else {
                        '.'
                    }
                })
                .collect::<String>();
            format!("{address:04X}  {hex}  {ascii}")
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn address_entry_accepts_only_a_16_bit_hex_address() {
        assert_eq!(parse_address("fF80"), Some(0xff80));
        assert_eq!(parse_address("0"), Some(0));
        for text in ["", "10000", "-1", "+1", "GG", " 1 "] {
            assert_eq!(parse_address(text), None, "{text}");
        }
    }

    #[test]
    fn memory_navigation_wraps_in_both_directions_and_home_returns_to_zero() {
        assert_eq!(navigate_memory(0xff80, "pagedown"), Some(0x0080));
        assert_eq!(navigate_memory(0, "pageup"), Some(0xff00));
        assert_eq!(navigate_memory(0xffff, "home"), Some(0));
        assert_eq!(navigate_memory(0, "x"), None);
    }

    #[test]
    fn rows_show_exact_bytes_printable_ascii_and_wrapped_addresses() {
        let memory = Memory {
            base_address: 0xfff0,
            bytes: (0..=255).collect(),
        };
        let rows = memory_rows(&memory);
        assert_eq!(rows.len(), 16);
        assert_eq!(
            rows[0],
            "FFF0  00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F  ................"
        );
        assert!(rows[1].starts_with("0000  10 11"));
        assert!(rows[2].ends_with(" !\"#$%&'()*+,-./"));
        assert!(rows[7].ends_with("pqrstuvwxyz{|}~."));
        assert!(rows[15].starts_with("00E0  F0 F1"));
    }
}
