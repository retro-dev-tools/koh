use super::Method;
use crate::{
    assembly::ExceptionRegion,
    cil::{Instruction, Operand},
    ir::Type,
};
use std::collections::{HashMap, HashSet};

pub(super) struct Normalized {
    pub instructions: Vec<Instruction>,
    pub sources: HashMap<u32, u32>,
    pub entries: HashSet<u32>,
}

fn contains(outer: &std::ops::Range<u32>, inner: &std::ops::Range<u32>) -> bool {
    outer.start <= inner.start && inner.end <= outer.end
}

pub(super) fn normalize(
    method: &mut Method<'_>,
    mut cil: Vec<Instruction>,
    regions: &[ExceptionRegion],
) -> Result<Normalized, String> {
    let mut result = Normalized {
        instructions: Vec::new(),
        sources: HashMap::new(),
        entries: HashSet::new(),
    };
    if regions.is_empty() {
        result.instructions = cil;
        return Ok(result);
    }
    if cil
        .len()
        .checked_mul(regions.len())
        .is_none_or(|work| work > 1_048_576)
        || regions.len() > 256
    {
        return Err("Finally control-flow validation exceeds resource limits".into());
    }
    let boundaries: HashSet<_> = cil
        .iter()
        .map(|i| i.offset)
        .chain([method.il.len() as u32])
        .collect();
    for region in regions {
        if region.handler.contains(&0) {
            return Err("Method entry cannot enter a finally handler".into());
        }
        if region.flags != 2 {
            return Err(format!(
                "Unsupported exception execution semantics: {}",
                match region.flags {
                    0 => "catch",
                    1 => "filter",
                    4 => "fault",
                    _ => "unknown clause",
                }
            ));
        }
        for range in [&region.protected, &region.handler] {
            if range.start >= range.end
                || range.end > method.il.len() as u32
                || !boundaries.contains(&range.start)
                || !boundaries.contains(&range.end)
            {
                return Err("Invalid finally instruction boundary/range".into());
            }
        }
        if region.protected.start < region.handler.end
            && region.handler.start < region.protected.end
        {
            return Err("Finally handler overlaps its protected region".into());
        }
        result
            .entries
            .extend([region.protected.start, region.handler.start]);
    }
    let ranges: Vec<_> = regions
        .iter()
        .flat_map(|r| [&r.protected, &r.handler])
        .collect();
    for (i, a) in ranges.iter().enumerate() {
        for b in &ranges[i + 1..] {
            if a == b {
                return Err("Unsupported shared finally region layout".into());
            }
            if a.start < b.end && b.start < a.end && !contains(a, b) && !contains(b, a) {
                return Err("Invalid partially overlapping finally regions".into());
            }
        }
    }
    for (index, outer) in regions.iter().enumerate() {
        for range in [&outer.protected, &outer.handler] {
            for (inner_index, inner) in regions.iter().enumerate() {
                if index != inner_index
                    && contains(range, &inner.protected) != contains(range, &inner.handler)
                {
                    return Err("Unsupported split finally region nesting".into());
                }
            }
        }
    }
    let transfer = |source: u32, target: u32, leave: bool| -> Result<(), String> {
        for region in regions {
            if region.handler.contains(&source) != region.handler.contains(&target) {
                return Err(format!(
                    "IL_{source:04x}: Control flow enters or exits a finally handler"
                ));
            }
            let from = region.protected.contains(&source);
            let to = region.protected.contains(&target);
            if from && !to && !leave || !from && to && target != region.protected.start {
                return Err(format!(
                    "IL_{source:04x}: Invalid protected-region transfer"
                ));
            }
        }
        Ok(())
    };
    let mut plans = Vec::new();
    let mut destinations: Vec<Vec<u32>> = vec![Vec::new(); regions.len()];
    for (index, instruction) in cil.iter().enumerate() {
        let local = match instruction.opcode {
            0x06..=0x09 => Some((instruction.opcode - 6) as usize),
            0x0a..=0x0d => Some((instruction.opcode - 0x0a) as usize),
            0x11..=0x13 | 0xfe0c..=0xfe0e => match instruction.operand {
                Operand::Variable(i) => Some(i as usize),
                _ => None,
            },
            _ => None,
        };
        if local.is_some_and(|i| i >= method.locals.len()) {
            return Err(format!(
                "IL_{:04x}: Invalid original local index",
                instruction.offset
            ));
        }
        let leave = matches!(instruction.opcode, 0xdd | 0xde);
        match &instruction.operand {
            Operand::Target(target) => transfer(instruction.offset, *target, leave)?,
            Operand::Targets(targets) => {
                for &target in targets {
                    transfer(instruction.offset, target, false)?;
                }
            }
            _ => {}
        }
        if instruction.opcode == 0x2a && ranges.iter().any(|r| r.contains(&instruction.offset)) {
            return Err(format!(
                "IL_{:04x}: ret cannot exit a protected region",
                instruction.offset
            ));
        }
        if instruction.opcode == 0xdc
            && !regions
                .iter()
                .any(|r| r.handler.contains(&instruction.offset))
        {
            return Err(format!(
                "IL_{:04x}: endfinally requires a finally handler",
                instruction.offset
            ));
        }
        if !matches!(
            instruction.opcode,
            0x27 | 0x2a | 0x2b | 0x38 | 0x7a | 0xdc..=0xde | 0xfe1a
        ) {
            let next = cil
                .get(index + 1)
                .ok_or("Finally method falls through its end")?;
            transfer(instruction.offset, next.offset, false)?;
        }
        if leave {
            let Operand::Target(target) = instruction.operand else {
                return Err("Missing leave destination".into());
            };
            let mut chain: Vec<_> = regions
                .iter()
                .enumerate()
                .filter(|(_, r)| {
                    r.protected.contains(&instruction.offset) && !r.protected.contains(&target)
                })
                .map(|(i, _)| i)
                .collect();
            chain.sort_by_key(|&i| regions[i].protected.end - regions[i].protected.start);
            let mut selectors = Vec::new();
            for (position, &handler) in chain.iter().enumerate() {
                let next = chain
                    .get(position + 1)
                    .map_or(target, |&i| regions[i].handler.start);
                let list = &mut destinations[handler];
                let selector = list.iter().position(|&t| t == next).unwrap_or_else(|| {
                    list.push(next);
                    list.len() - 1
                });
                selectors.push((handler, selector));
            }
            if !chain.is_empty() {
                plans.push((index, selectors, regions[chain[0]].handler.start));
            }
        }
    }
    if method.locals.len() + method.parameters.len() + regions.len() > 1024 {
        return Err("Finally continuation locals exceed1024 slots".into());
    }
    let local_base = method.locals.len();
    method
        .locals
        .extend(std::iter::repeat_n(Type::I32, regions.len()));
    let mut tail = Vec::new();
    let original_count = cil.len();
    let base = method.il.len() as u32;
    let mut append = |opcode, operand, source| -> Result<u32, String> {
        if original_count + tail.len() >= 16_383 {
            return Err("Finally expansion exceeds16383 instructions".into());
        }
        let offset = base + tail.len() as u32;
        tail.push(Instruction {
            offset,
            opcode,
            operand,
        });
        result.sources.insert(offset, source);
        Ok(offset)
    };
    for (index, selectors, entry) in plans {
        let source = cil[index].offset;
        let mut first = None;
        for (handler, selector) in selectors {
            let offset = append(0x20, Operand::Integer(selector as i64), source)?;
            first.get_or_insert(offset);
            append(
                0xfe0e,
                Operand::Variable((local_base + handler) as u16),
                source,
            )?;
        }
        append(0x38, Operand::Target(entry), source)?;
        cil[index].operand = Operand::Target(first.ok_or("Missing finally continuation")?);
    }
    for instruction in &mut cil[..original_count] {
        if instruction.opcode != 0xdc {
            continue;
        }
        let handler = regions
            .iter()
            .enumerate()
            .filter(|(_, r)| r.handler.contains(&instruction.offset))
            .min_by_key(|(_, r)| r.handler.end - r.handler.start)
            .map(|(i, _)| i)
            .ok_or("Missing endfinally owner")?;
        if regions.iter().any(|r| {
            r.protected.contains(&instruction.offset)
                && contains(&regions[handler].handler, &r.protected)
        }) {
            return Err("endfinally cannot exit a nested protected region".into());
        }
        let targets = &destinations[handler];
        if targets.is_empty() {
            continue;
        }
        let first = append(
            0xfe0c,
            Operand::Variable((local_base + handler) as u16),
            instruction.offset,
        )?;
        append(0x45, Operand::Targets(targets.clone()), instruction.offset)?;
        append(0x38, Operand::Target(targets[0]), instruction.offset)?;
        instruction.operand = Operand::Target(first);
    }
    cil.extend(tail);
    result.instructions = cil;
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{ir, resolve::Definition};
    use rstest::rstest;

    fn region() -> ExceptionRegion {
        ExceptionRegion {
            flags: 2,
            protected: 0..3,
            handler: 3..5,
        }
    }

    fn method(il: &[u8], max_stack: u16) -> Method<'_> {
        Method {
            identity: Definition {
                assembly: 0,
                token: 0x06000001,
            },
            il,
            parameters: vec![],
            returns: None,
            locals: vec![],
            init_locals: false,
            max_stack,
        }
    }

    #[rstest]
    #[case(0, false)]
    #[case(1, false)]
    #[case(1, true)]
    fn internal_continuations_preserve_source_offsets_and_stack_limits(
        #[case] max_stack: u16,
        #[case] extra: bool,
    ) {
        let bytes = [0, 0xde, 2, if extra { 0x16 } else { 0 }, 0xdc, 0x2a];
        let function =
            super::super::lower_with(method(&bytes, max_stack), None, &[], &[], None, &[region()])
                .unwrap();
        ir::verify(&function).unwrap();
        for block in &function.blocks {
            assert!(block.terminator.il_offset < bytes.len() as u32);
            for instruction in &block.instructions {
                assert!(instruction.il_offset < bytes.len() as u32);
            }
        }
    }

    #[rstest]
    #[case(&[0,0x2b,2,0,0xdc,0x2a], "protected-region transfer")]
    #[case(&[0,0xde,0,0,0xdc,0x2a], "finally handler")]
    #[case(&[0x2a,0xde,2,0,0xdc,0x2a], "ret cannot exit")]
    #[case(&[0xdc,0xde,2,0,0xdc,0x2a], "endfinally requires")]
    #[case(&[0,0xde,2,0x26,0xdc,0x2a], "stack underflow")]
    #[case(&[6,0xde,2,0,0xdc,0x2a], "Invalid original local")]
    fn invalid_finally_control_flow_diagnoses(#[case] bytes: &[u8], #[case] diagnostic: &str) {
        let error = super::super::lower_with(method(bytes, 1), None, &[], &[], None, &[region()])
            .unwrap_err();
        assert!(error.contains(diagnostic), "{error}");
    }

    #[rstest]
    #[case(0, "catch")]
    #[case(1, "filter")]
    #[case(4, "fault")]
    fn unsupported_execution_is_distinct_from_bad_metadata(#[case] flags: u32, #[case] name: &str) {
        let mut region = region();
        region.flags = flags;
        let error = super::super::lower_with(
            method(&[0, 0xde, 2, 0, 0xdc, 0x2a], 1),
            None,
            &[],
            &[],
            None,
            &[region],
        )
        .unwrap_err();
        assert!(
            error.contains("Unsupported exception execution semantics") && error.contains(name),
            "{error}"
        );
    }

    #[rstest]
    #[case(0..2,3..5,"boundary")]
    #[case(0..4,3..5,"overlaps")]
    #[case(3..5,0..3,"entry")]
    fn invalid_region_shapes_diagnose(
        #[case] protected: std::ops::Range<u32>,
        #[case] handler: std::ops::Range<u32>,
        #[case] expected: &str,
    ) {
        let region = ExceptionRegion {
            flags: 2,
            protected,
            handler,
        };
        let error = super::super::lower_with(
            method(&[0, 0xde, 2, 0, 0xdc, 0x2a], 1),
            None,
            &[],
            &[],
            None,
            &[region],
        )
        .unwrap_err();
        assert!(error.contains(expected), "{error}");
    }

    #[rstest]
    #[case(0..10,10..15,"shared")]
    #[case(5..12,15..20,"partially overlapping")]
    #[case(1..5,15..20,"split")]
    fn shared_partial_and_split_layouts_reject(
        #[case] protected: std::ops::Range<u32>,
        #[case] handler: std::ops::Range<u32>,
        #[case] diagnostic: &str,
    ) {
        let bytes = vec![0; 31];
        let mut input = method(&bytes, 1);
        let regions = [
            ExceptionRegion {
                flags: 2,
                protected: 0..10,
                handler: 10..15,
            },
            ExceptionRegion {
                flags: 2,
                protected,
                handler,
            },
        ];
        let error = normalize(&mut input, crate::cil::decode(&bytes).unwrap(), &regions)
            .err()
            .unwrap();
        assert!(error.contains(diagnostic), "{error}");
    }

    #[test]
    fn original_stack_and_initialization_contracts_are_not_relaxed() {
        let bytes = [0x16, 0x26, 0xde, 2, 0, 0xdc, 0x2a];
        let region = ExceptionRegion {
            flags: 2,
            protected: 0..4,
            handler: 4..6,
        };
        let error = super::super::lower_with(method(&bytes, 0), None, &[], &[], None, &[region])
            .unwrap_err();
        assert!(error.contains("declared maxstack"), "{error}");
        let bytes = [0x06, 0x26, 0xde, 2, 0, 0xdc, 0x2a];
        let mut input = method(&bytes, 1);
        input.locals.push(Type::I32);
        let region = ExceptionRegion {
            flags: 2,
            protected: 0..4,
            handler: 4..6,
        };
        let error = super::super::lower_with(input, None, &[], &[], None, &[region]).unwrap_err();
        assert!(
            error.contains("Local load lacks definite assignment"),
            "{error}"
        );
        let bytes = [0x16, 0, 0xde, 2, 0, 0xdc, 0x26, 0x2a];
        let region = ExceptionRegion {
            flags: 2,
            protected: 1..4,
            handler: 4..6,
        };
        let error = super::super::lower_with(method(&bytes, 1), None, &[], &[], None, &[region])
            .unwrap_err();
        assert!(error.contains("Protected region entry"), "{error}");
    }

    #[test]
    fn leave_can_enter_the_start_of_a_disjoint_protected_block() {
        let bytes = [0, 0xde, 2, 0, 0xdc, 0, 0xde, 2, 0, 0xdc, 0x2a];
        let regions = [
            region(),
            ExceptionRegion {
                flags: 2,
                protected: 5..8,
                handler: 8..10,
            },
        ];
        let function =
            super::super::lower_with(method(&bytes, 0), None, &[], &[], None, &regions).unwrap();
        ir::verify(&function).unwrap();
    }
}
