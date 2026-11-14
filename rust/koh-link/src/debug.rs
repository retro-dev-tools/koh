use crate::LinkResult;
use std::collections::HashMap;

fn u32le(out: &mut Vec<u8>, value: u32) {
    out.extend(value.to_le_bytes());
}
fn intern(values: &mut Vec<String>, map: &mut HashMap<String, u32>, value: &str) -> u32 {
    if let Some(id) = map.get(value) {
        return *id;
    }
    let id = values.len() as u32 + 1;
    values.push(value.into());
    map.insert(value.into(), id);
    id
}
pub fn write(result: &LinkResult) -> Result<Vec<u8>, String> {
    let mut strings = Vec::new();
    let mut string_ids = HashMap::new();
    let mut files = Vec::new();
    let mut file_ids = HashMap::new();
    for s in &result.symbols {
        intern(&mut strings, &mut string_ids, &s.name);
        if let Some(p) = &s.source {
            intern(&mut files, &mut file_ids, &p.file);
        }
    }
    for l in &result.lines {
        intern(&mut files, &mut file_ids, &l.file);
        for frame in &l.expansion {
            intern(&mut files, &mut file_ids, &frame.file);
        }
    }
    for f in &files {
        intern(&mut strings, &mut string_ids, f);
    }
    let mut pool = Vec::new();
    u32le(&mut pool, strings.len() as u32);
    for s in &strings {
        let n = u16::try_from(s.len()).map_err(|_| "KDBG string exceeds 65535 bytes")?;
        pool.extend(n.to_le_bytes());
        pool.extend(s.as_bytes());
    }
    let mut sources = Vec::new();
    u32le(&mut sources, files.len() as u32);
    for f in &files {
        u32le(&mut sources, string_ids[f]);
    }
    let mut symbols = Vec::new();
    u32le(&mut symbols, result.symbols.len() as u32);
    for s in &result.symbols {
        let bank = u8::try_from(s.bank.unwrap_or(0))
            .map_err(|_| "KDBG v1 cannot represent banks above 255")?;
        let address = u16::try_from(s.value)
            .map_err(|_| "KDBG v1 cannot represent symbol values outside 0..65535")?;
        symbols.push(s.kind);
        symbols.push(bank);
        symbols.extend(address.to_le_bytes());
        symbols.extend(0u16.to_le_bytes());
        symbols.extend(0u16.to_le_bytes());
        u32le(&mut symbols, string_ids[&s.name]);
        u32le(&mut symbols, 0);
        u32le(
            &mut symbols,
            s.source.as_ref().map_or(0, |p| file_ids[&p.file]),
        );
        u32le(&mut symbols, s.source.as_ref().map_or(0, |p| p.line));
    }
    let mut mappings = Vec::new();
    let mut count = 0u32;
    let mut expansions = Vec::new();
    let mut expansion_fixups = Vec::new();
    for l in &result.lines {
        let bank = u8::try_from(l.bank).map_err(|_| "KDBG v1 cannot represent banks above 255")?;
        let mut offset = 0;
        let expansion = if l.expansion.is_empty() {
            None
        } else {
            let at = expansions.len() as u32;
            expansions.extend(
                u16::try_from(l.expansion.len())
                    .map_err(|_| "KDBG expansion exceeds 65535 frames")?
                    .to_le_bytes(),
            );
            for frame in &l.expansion {
                u32le(&mut expansions, file_ids[&frame.file]);
                u32le(&mut expansions, frame.line);
            }
            Some(at)
        };
        while offset < l.count {
            let n = (l.count - offset).min(255) as u8;
            let address =
                u16::try_from(l.address + offset).map_err(|_| "KDBG address exceeds 16 bits")?;
            mappings.push(bank);
            mappings.push(n);
            mappings.extend(address.to_le_bytes());
            u32le(&mut mappings, file_ids[&l.file]);
            u32le(&mut mappings, l.line);
            if let Some(at) = expansion {
                expansion_fixups.push((mappings.len(), at));
            }
            u32le(&mut mappings, u32::MAX);
            count = count.checked_add(1).ok_or("Too many KDBG mappings")?;
            offset += u32::from(n);
        }
    }
    let map_offset = 32 + pool.len() as u32 + sources.len() as u32 + symbols.len() as u32;
    let expansion_offset = if expansions.is_empty() {
        0
    } else {
        map_offset + 4 + mappings.len() as u32
    };
    for (at, relative) in expansion_fixups {
        mappings[at..at + 4].copy_from_slice(&(expansion_offset + 4 + relative).to_le_bytes());
    }
    let offsets = [
        32u32,
        32 + pool.len() as u32,
        0,
        32 + pool.len() as u32 + sources.len() as u32,
        map_offset,
        expansion_offset,
    ];
    let mut out = b"KDBG".to_vec();
    out.extend(1u16.to_le_bytes());
    out.extend(u16::from(!expansions.is_empty()).to_le_bytes());
    for o in offsets {
        u32le(&mut out, o);
    }
    out.extend(pool);
    out.extend(sources);
    out.extend(symbols);
    u32le(&mut out, count);
    out.extend(mappings);
    if !expansions.is_empty() {
        u32le(&mut out, expansions.len() as u32);
        out.extend(expansions);
    }
    Ok(out)
}
