use koh_compile::{ir::*, resolve::Definition, sm83};
use koh_objects::*;
use rstest::fixture;
use std::path::Path;
#[path = "support/allocation_contracts.rs"]
mod allocation_contracts;
#[path = "support/compare_branch.rs"]
mod compare_branch;
#[path = "support/control_flow_scalars.rs"]
mod control_flow_scalars;
#[path = "support/liveness.rs"]
mod liveness;
#[path = "support/loop_contracts.rs"]
mod loop_contracts;
#[path = "support/narrowing.rs"]
mod narrowing;
#[path = "support/optimization.rs"]
mod optimization;
#[path = "support/optimizer_contracts.rs"]
mod optimizer_contracts;
#[path = "support/parallel_phis.rs"]
mod parallel_phis;
#[path = "support/recursion.rs"]
mod recursion_fixture;
#[path = "support/rewrites.rs"]
mod rewrites;
#[path = "support/sum_loop.rs"]
mod sum_loop;
mod support;
use support::{Host, dotnet};

#[rstest::rstest]
#[case("EmptyCost", 0u64, 1u64)]
#[case("CountedCost", 99u64, 100u64)]
#[case("CopyCost", 0u64, 32u64)]
fn real_loop_cost_baselines(
    host: &Host,
    #[case] name: &str,
    #[case] expected: u64,
    #[case] iterations: u64,
) {
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/RewriteContracts");
    let output = dotnet()
        .arg("build")
        .arg(fixture.join("RewriteContracts.csproj"))
        .args(["-c", "Release", "--nologo", "-v:q", "-m:1"])
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let image = std::fs::read(fixture.join("bin/Release/net10.0/RewriteContracts.dll")).unwrap();
    let resolver = koh_compile::resolve::Resolver::new(&[&image]).unwrap();
    let metadata = resolver.assembly(0).unwrap().metadata();
    let identity = Definition {
        assembly: 0,
        token: 0x06000001
            + metadata
                .method_defs
                .iter()
                .position(|m| metadata.strings.get(m.name).unwrap() == name)
                .unwrap() as u32,
    };
    let mut function = koh_compile::lower::assembly_method(&resolver, identity).unwrap();
    koh_compile::optimize::simplify(&mut function).unwrap();
    verify_program(&[function.clone()]).unwrap();
    let directory = tempfile::tempdir_in(host.directory.path()).unwrap();
    let path = directory.path().join("loop.gb");
    std::fs::write(&path, rom_program(&[function], identity, &[], 0xd000)).unwrap();
    let output = dotnet()
        .arg(&host.dll)
        .arg("--loop-cost")
        .arg(path)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let text = String::from_utf8(output.stdout).unwrap();
    let fields: Vec<_> = text.split_whitespace().collect();
    assert_eq!(fields[0], "A5", "{text}");
    assert_eq!(
        fields[2],
        expected
            .to_le_bytes()
            .iter()
            .map(|b| format!("{b:02X}"))
            .collect::<String>(),
        "{text}"
    );
    assert_eq!(&fields[3..5], &["D000", "OK"], "{text}");
    let cycles: u64 = fields[5].parse().unwrap();
    let window: u64 = fields[6].parse().unwrap();
    assert!(window > 0 && window < cycles, "{text}");
    println!(
        "{name}: {cycles} T-cycles total; {:.3} per iteration including harness and result observation",
        cycles as f64 / iterations as f64
    );
    println!(
        "{name}: {window} T-cycles in marked source window; {:.3} per iteration",
        window as f64 / iterations as f64
    );
}

#[rstest::rstest]
#[case("Debug")]
#[case("Release")]
fn machine_rewrites_preserve_carry_control_and_observed_memory(
    host: &Host,
    #[case] configuration: &str,
) {
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/RewriteContracts");
    let output = dotnet()
        .arg("build")
        .arg(fixture.join("RewriteContracts.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let image =
        std::fs::read(fixture.join(format!("bin/{configuration}/net10.0/RewriteContracts.dll")))
            .unwrap();
    let resolver = koh_compile::resolve::Resolver::new(&[&image]).unwrap();
    let metadata = resolver.assembly(0).unwrap().metadata();
    let identity = |name: &str| Definition {
        assembly: 0,
        token: 0x06000001
            + metadata
                .method_defs
                .iter()
                .position(|m| metadata.strings.get(m.name).unwrap() == name)
                .unwrap() as u32,
    };
    let mut executions = 0;
    for shape in rewrites::SHAPES
        .iter()
        .copied()
        .chain(["Carry", "Borrow", "Shifts"])
    {
        let original = koh_compile::lower::assembly_method(&resolver, identity(shape)).unwrap();
        let inputs: Vec<_> = match shape {
            _ if shape.starts_with("Loop") => {
                let inputs = if shape == "LoopNested" {
                    rewrites::NESTED_INPUTS
                } else {
                    rewrites::LOOP_INPUTS
                };
                inputs
                    .iter()
                    .map(|&(x, y)| (u64::from(x), u64::from(y)))
                    .collect()
            }
            "SequentialCopy" => rewrites::SEQUENTIAL_INPUTS
                .iter()
                .map(|&(x, y)| (u64::from(x), u64::from(y)))
                .collect(),
            "GuardedCopy" => rewrites::GUARD_INPUTS
                .iter()
                .map(|&(x, y)| (u64::from(x), u64::from(y)))
                .collect(),
            "Carry" | "Borrow" => rewrites::WIDE.to_vec(),
            "Shifts" => [0x8000000000000001, 0x123456789abcdef0]
                .into_iter()
                .flat_map(|x| {
                    [0, 1, 7, 8, 15, 31, 32, 33, 63, 64, 65, 127]
                        .into_iter()
                        .map(move |y| (x, y))
                })
                .collect(),
            _ if shape.starts_with("Indexed") => rewrites::INDEX_INPUTS
                .iter()
                .map(|&(x, y)| (u64::from(x), u64::from(y)))
                .collect(),
            _ if shape.starts_with("Walk") => rewrites::WALK_INPUTS
                .iter()
                .map(|&(x, y)| (u64::from(x), u64::from(y)))
                .collect(),
            _ => rewrites::INPUTS
                .iter()
                .map(|&(x, y)| (u64::from(x), u64::from(y)))
                .collect(),
        };
        let mut cases = Vec::new();
        let mut observed = Vec::new();
        for (x, y) in inputs {
            let (value, trace) = match shape {
                "Carry" => (x.wrapping_add(y), "-".into()),
                "Borrow" => (x.wrapping_sub(y), "-".into()),
                "Shifts" => (
                    (x << ((y & 63) as u32)) ^ (x >> ((y & 63) as u32)),
                    "-".into(),
                ),
                _ => rewrites::expected(shape, x as u32, y as u32),
            };
            let arguments = vec![x, y];
            for (root, arguments) in [
                (original.clone(), arguments.clone()),
                (
                    control_flow_scalars::constant_inputs(original.clone(), &arguments),
                    vec![],
                ),
            ] {
                let mut functions = vec![root];
                for helper in match shape {
                    "LoopCall" => &["Touch"][..],
                    "GuardedCopy" | "SequentialCopy" => &["Drip"][..],
                    "Call" => &["Change"][..],
                    "Tail" | "NotTail" | "ReturnJoin" | "IndexedDelayed" => &["Leaf"][..],
                    _ if shape.starts_with("Walk") => &["SeedWalk"][..],
                    _ => &[],
                } {
                    functions.push(
                        koh_compile::lower::assembly_method(&resolver, identity(helper)).unwrap(),
                    );
                }
                for optimized in [false, true] {
                    if optimized {
                        for function in &mut functions {
                            koh_compile::optimize::simplify(function).unwrap();
                        }
                    }
                    verify_program(&functions).unwrap();
                    cases.push((
                        rom_program(&functions, identity(shape), &arguments, 0xd000),
                        Ok(value),
                    ));
                    observed.push(trace.clone());
                }
            }
        }
        executions += cases.len();
        run_roms_observed(host, &cases, 0xd000, Some(&observed));
    }
    assert_eq!(executions, 1224);
}

#[rstest::rstest]
#[case("Debug", false)]
#[case("Release", false)]
#[case("Debug", true)]
#[case("Release", true)]
fn mapper_writes_and_calls_preserve_physical_rom_reads(
    host: &Host,
    #[case] configuration: &str,
    #[case] high: bool,
) {
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/BankingContracts");
    let output = dotnet()
        .arg("build")
        .arg(fixture.join("BankingContracts.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let image =
        std::fs::read(fixture.join(format!("bin/{configuration}/net10.0/BankingContracts.dll")))
            .unwrap();
    let resolver = koh_compile::resolve::Resolver::new(&[&image]).unwrap();
    let metadata = resolver.assembly(0).unwrap().metadata();
    let identity = |name: &str| Definition {
        assembly: 0,
        token: 0x06000001
            + metadata
                .method_defs
                .iter()
                .position(|m| metadata.strings.get(m.name).unwrap() == name)
                .unwrap() as u32,
    };
    let inputs: &[(&str, u64, u64)] = if high {
        &[("HighBit", 0, 0x31a331)]
    } else {
        &[
            ("Same", 0, 0x3131),
            ("Different", 0, 0x3142),
            ("Call", 0, 0x314231),
            ("Ordinary", 0, 0x312a),
            ("OtherRegister", 0, 0x31),
            ("Variable", 0, 0x310731),
            ("Variable", 3, 0x313131),
            ("Variable", 4, 0x314231),
            ("Variable", 255, 0x31f531),
        ]
    };
    let mut executions = 0;
    for &(shape, bank, expected) in inputs {
        let mut banks = vec![(0, 7), (3, 0x31), (4, 0x42)];
        if high {
            banks.push((259, 0xa3));
        }
        if bank == 255 {
            banks.push((255, 0xf5));
        }
        let data = Object {
            sections: banks
                .iter()
                .map(|&(bank, value)| Section {
                    name: format!(".fixture.bank.{bank}"),
                    kind: if bank == 0 { 0 } else { 1 },
                    address: Some(if bank == 0 { 0x3ff0 } else { 0x7ff0 }),
                    bank: Some(bank),
                    size: 1,
                    data: vec![value],
                    ..Default::default()
                })
                .collect(),
            ..Default::default()
        };
        let data = read_elf(&write_elf(&data).unwrap()).unwrap();
        let original = koh_compile::lower::assembly_method(&resolver, identity(shape)).unwrap();
        let mut cases = Vec::new();
        for (root, arguments) in [
            (original.clone(), vec![bank]),
            (
                control_flow_scalars::constant_inputs(original, &[bank]),
                vec![],
            ),
        ] {
            let mut functions = vec![root];
            if shape == "Call" {
                functions.push(
                    koh_compile::lower::assembly_method(&resolver, identity("Change")).unwrap(),
                );
            }
            for optimized in [false, true] {
                if optimized {
                    for function in &mut functions {
                        koh_compile::optimize::simplify(function).unwrap();
                    }
                }
                verify_program(&functions).unwrap();
                for function in &functions {
                    assert!(
                        sm83::emit(function)
                            .unwrap()
                            .sections
                            .iter()
                            .filter(|s| s.name.starts_with(".text."))
                            .all(|s| s.kind == 0),
                        "Mapper-changing fixture code must stay in ROM0"
                    );
                }
                let rom = rom_program_data(
                    &functions,
                    identity(shape),
                    &arguments,
                    0xd000,
                    std::slice::from_ref(&data),
                );
                assert_eq!(rom[0x147], 0x19);
                for &(bank, value) in &banks {
                    assert_eq!(rom[bank as usize * 0x4000 + 0x3ff0], value);
                }
                cases.push((rom, Ok(expected)));
            }
        }
        executions += cases.len();
        run_roms(host, &cases, 0xd000);
    }
    assert_eq!(executions, if high { 4 } else { 36 });
}

#[rstest::rstest]
#[case("Debug")]
#[case("Release")]
fn optimization_preserves_arithmetic_aliases_and_effects(host: &Host, #[case] configuration: &str) {
    let fixture =
        Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/OptimizationContracts");
    let output = dotnet()
        .arg("build")
        .arg(fixture.join("OptimizationContracts.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let image = std::fs::read(fixture.join(format!(
        "bin/{configuration}/net10.0/OptimizationContracts.dll"
    )))
    .unwrap();
    let resolver = koh_compile::resolve::Resolver::new(&[&image]).unwrap();
    let metadata = resolver.assembly(0).unwrap().metadata();
    let identity = |name: &str| Definition {
        assembly: 0,
        token: 0x06000001
            + metadata
                .method_defs
                .iter()
                .position(|m| metadata.strings.get(m.name).unwrap() == name)
                .unwrap() as u32,
    };
    let mut cases = Vec::new();
    for &shape in optimization::SHAPES {
        let original = koh_compile::lower::assembly_method(&resolver, identity(shape))
            .unwrap_or_else(|e| panic!("{shape}: {e}"));
        for &(x, y) in optimization::INPUTS {
            let arguments = vec![u64::from(x), u64::from(y)];
            for (root, arguments) in [
                (original.clone(), arguments.clone()),
                (
                    control_flow_scalars::constant_inputs(original.clone(), &arguments),
                    vec![],
                ),
            ] {
                let mut functions = vec![root];
                for helper in match shape {
                    "Escaped" => &["Change"][..],
                    "InlineLeaf" => &["AddThree"][..],
                    _ => &[],
                } {
                    functions.push(
                        koh_compile::lower::assembly_method(&resolver, identity(helper)).unwrap(),
                    );
                }
                for optimized in [false, true] {
                    if optimized {
                        for f in &mut functions {
                            koh_compile::optimize::simplify(f).unwrap();
                        }
                    }
                    verify_program(&functions).unwrap();
                    if shape == "UnusedBusRead" {
                        assert!(
                            functions[0]
                                .blocks
                                .iter()
                                .flat_map(|b| &b.instructions)
                                .any(|i| matches!(i.op, Op::LoadMemory { .. })),
                            "Native bus reads must survive DCE"
                        );
                    }
                    cases.push((
                        rom_program(&functions, identity(shape), &arguments, 0xd000),
                        optimization::expected(shape, x, y),
                    ));
                }
            }
        }
    }
    assert_eq!(cases.len(), 1344);
    assert_eq!(
        cases.iter().filter(|(_, result)| *result == Err(2)).count(),
        32
    );
    assert_eq!(
        cases.iter().filter(|(_, result)| *result == Err(4)).count(),
        48
    );
    for cases in cases.chunks(32) {
        run_roms(host, cases, 0xd000);
    }
    let error = koh_compile::compile::reachable(&resolver, identity("Dead")).unwrap_err();
    assert!(error.contains("Dead") && error.contains("IL_"), "{error}");
    assert!(
        error.contains("System.Runtime") || error.contains("opcode"),
        "{error}"
    );
    let reachable = koh_compile::compile::reachable(&resolver, identity("InlineLeaf")).unwrap();
    assert_eq!(reachable.len(), 2);
    assert!(
        reachable
            .iter()
            .all(|f| [identity("InlineLeaf"), identity("AddThree")].contains(&f.identity))
    );
}

#[rstest::rstest]
#[case(false)]
#[case(true)]
fn optimization_preserves_dead_and_store_reachable_phi_cycles(host: &Host, #[case] stored: bool) {
    let mut original = parallel_phis::swap(Type::I32);
    original.blocks[3].terminator.kind = Control::Return(Some(6));
    if stored {
        original.locals.push(Type::I32);
        original.blocks[0].instructions.push(Instruction {
            result: None,
            op: Op::StoreLocal { local: 0, value: 0 },
            il_offset: 0,
        });
        original.blocks[2].instructions.push(Instruction {
            result: None,
            op: Op::StoreLocal { local: 0, value: 4 },
            il_offset: 2,
        });
        original.blocks[3].instructions.push(Instruction {
            result: Some((10, Type::I32)),
            op: Op::LoadLocal(0),
            il_offset: 3,
        });
        original.blocks[3].terminator.kind = Control::Return(Some(10));
    }
    let mut cases = Vec::new();
    for (x, y, n) in [
        (1, 2, 0),
        (1, 2, 1),
        (1, 2, 2),
        (0xffff, 0x1234, 7),
        (u32::MAX as u64, 0, 8),
    ] {
        let expected = if stored {
            if n > 0 && n % 2 == 0 { y } else { x }
        } else {
            n
        };
        let arguments = vec![x, y, n];
        for (root, arguments) in [
            (original.clone(), arguments.clone()),
            (
                control_flow_scalars::constant_inputs(original.clone(), &arguments),
                vec![],
            ),
        ] {
            let mut optimized = root.clone();
            koh_compile::optimize::simplify(&mut optimized).unwrap();
            for function in [root, optimized] {
                verify(&function).unwrap();
                cases.push((function, arguments.clone(), Ok(expected)));
            }
        }
    }
    assert_eq!(cases.len(), 20);
    run(host, &cases);
}

#[rstest::rstest]
#[case("Debug")]
#[case("Release")]
fn loop_optimization_preserves_control_memory_and_dominance(
    host: &Host,
    #[case] configuration: &str,
) {
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/LoopContracts");
    let output = dotnet()
        .arg("build")
        .arg(fixture.join("LoopContracts.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let image =
        std::fs::read(fixture.join(format!("bin/{configuration}/net10.0/LoopContracts.dll")))
            .unwrap();
    let resolver = koh_compile::resolve::Resolver::new(&[&image]).unwrap();
    let mut cases = Vec::new();
    for shape in ["Invariant", "Variant", "Loaded", "Nested", "Division"] {
        let metadata = resolver.assembly(0).unwrap().metadata();
        let index = metadata
            .method_defs
            .iter()
            .position(|m| metadata.strings.get(m.name).unwrap() == shape)
            .unwrap();
        let identity = Definition {
            assembly: 0,
            token: 0x06000001 + index as u32,
        };
        let original = koh_compile::lower::assembly_method(&resolver, identity).unwrap();
        let mut once = original.clone();
        koh_compile::optimize::simplify(&mut once).unwrap();
        let mut twice = once.clone();
        koh_compile::optimize::simplify(&mut twice).unwrap();
        for function in [original, once, twice] {
            verify(&function).unwrap();
            for &(n, seed, flag) in loop_contracts::INPUTS {
                cases.push((
                    function.clone(),
                    vec![n as u32 as u64, seed as u32 as u64, flag as u32 as u64],
                    loop_contracts::expected(shape, n, seed, flag),
                ));
            }
        }
    }
    assert_eq!(cases.len(), 210);
    assert_eq!(
        cases
            .iter()
            .filter(|(_, _, result)| result.is_err())
            .count(),
        6
    );
    for cases in cases.chunks(32) {
        run(host, cases);
    }
}

#[rstest::rstest]
#[case(8, false)]
#[case(16, false)]
#[case(8, true)]
#[case(16, true)]
fn narrowing_preserves_comparisons_and_every_escaping_width(
    host: &Host,
    #[case] bits: u8,
    #[case] comparisons: bool,
) {
    let shapes: &[&str] = if comparisons {
        &[
            "zlt",
            "zult",
            "slt",
            "zge",
            "sult",
            "zeq",
            "seq",
            "mixed-eq",
            "constant-lt",
        ]
    } else {
        &[
            "sum",
            "mixed",
            "constant",
            "chain",
            "wide-sum",
            "wide-chain",
            "two-widths",
            "shift",
            "divide",
        ]
    };
    let mut cases = Vec::new();
    for &shape in shapes {
        let original = narrowing::expression(bits, shape);
        for &arguments in narrowing::INPUTS {
            let expected = narrowing::expected(bits, shape, arguments);
            for (function, args) in [
                (original.clone(), arguments.to_vec()),
                (
                    control_flow_scalars::constant_inputs(original.clone(), &arguments),
                    vec![],
                ),
            ] {
                let mut optimized = function.clone();
                koh_compile::optimize::simplify(&mut optimized).unwrap();
                for function in [function, optimized] {
                    verify(&function).unwrap();
                    cases.push((function, args.clone(), expected));
                }
            }
        }
    }
    assert_eq!(cases.len(), 9 * 22 * 4);
    for cases in cases.chunks(32) {
        run(host, cases);
    }
}

#[rstest::rstest]
fn byte_counter_backedges_preserve_wrapping_without_narrowing_the_trip_count(host: &Host) {
    let mut cases = Vec::new();
    for (direct, inputs) in [
        (
            false,
            &[
                (0, 10, 10, 10),
                (250, 10, 4, 10),
                (1, 0, 1, 0),
                (127, 1, 128, 1),
                (255, 1, 0, 1),
                (255, 256, 255, 256),
                (0, 260, 4, 260),
                (0xff0000, 257, 1, 257),
            ][..],
        ),
        (
            true,
            &[
                (0, 10, 10, 10),
                (250, 255, 255, 5),
                (255, 255, 255, 0),
                (1, 0, 1, 0),
                (250, 10, 250, 0),
                (127, 129, 129, 2),
            ][..],
        ),
    ] {
        let original = narrowing::counter(direct);
        for &(start, count, expected, iterations) in inputs {
            let expected = (iterations << 32) | expected;
            for (function, arguments) in [
                (original.clone(), vec![start, count]),
                (
                    control_flow_scalars::constant_inputs(original.clone(), &[start, count]),
                    vec![],
                ),
            ] {
                let mut optimized = function.clone();
                koh_compile::optimize::simplify(&mut optimized).unwrap();
                for function in [function, optimized] {
                    cases.push((function, arguments.clone(), Ok(expected)));
                }
            }
        }
    }
    assert_eq!(cases.len(), 56);
    run(host, &cases);
}

#[rstest::rstest]
#[case(8)]
#[case(16)]
#[case(32)]
#[case(64)]
fn allocation_choices_preserve_arithmetic_and_live_producer_graphs(host: &Host, #[case] bits: u8) {
    let mut cases = Vec::new();
    let inputs = allocation_contracts::cases(bits);
    assert_eq!(
        inputs.len(),
        match bits {
            8 => 14,
            16 => 16,
            32 => 18,
            64 => 22,
            _ => unreachable!(),
        }
    );
    for (f, arguments, expected) in inputs {
        for (f, arguments) in [
            (f.clone(), arguments.clone()),
            (control_flow_scalars::constant_inputs(f, &arguments), vec![]),
        ] {
            let mut optimized = f.clone();
            koh_compile::optimize::simplify(&mut optimized).unwrap();
            for f in [f, optimized] {
                verify(&f).unwrap();
                cases.push((f, arguments.clone(), Ok(expected)));
            }
        }
    }
    for batch in cases.chunks(32) {
        run(host, batch);
    }
}

#[rstest::rstest]
fn loop_backedges_preserve_arguments_across_clobbering_operations(host: &Host) {
    let original = allocation_contracts::parameter_loop();
    let mut optimized = original.clone();
    koh_compile::optimize::simplify(&mut optimized).unwrap();
    let mut cases = Vec::new();
    for (value, iterations, expected) in [(127, 1, 128), (127, 3, 128), (3, 4, 12), (255, 2, 0)] {
        for f in [&original, &optimized] {
            cases.push((f.clone(), vec![value, iterations], Ok(expected)));
        }
    }
    run(host, &cases);
}

#[rstest::rstest]
#[case(Type::Native)]
#[case(Type::I32)]
#[case(Type::I64)]
fn predicate_normalization_preserves_order_signedness_and_equality(host: &Host, #[case] ty: Type) {
    let bits = ty.bits();
    let mask = u64::MAX >> (64 - bits);
    let sign = 1u64 << (bits - 1);
    let mut cases = Vec::new();
    for (op, invert, a, b, expected) in [
        (Comparison::GreaterUnsigned, false, 1, 2, 0),
        (Comparison::GreaterUnsigned, false, 2, 1, 1),
        (Comparison::GreaterUnsigned, false, mask, 1, 1),
        (Comparison::GreaterUnsigned, false, 1, mask, 0),
        (Comparison::GreaterUnsigned, false, sign, sign, 0),
        (Comparison::GreaterSigned, true, mask, 0, 1),
        (Comparison::GreaterSigned, true, 0, mask, 0),
        (Comparison::GreaterSigned, true, sign, sign, 1),
        (Comparison::GreaterSigned, true, sign, 1, 1),
        (Comparison::GreaterSigned, true, 1, sign, 0),
        (Comparison::GreaterSigned, true, mask, mask, 1),
        (Comparison::Equal, false, sign, sign, 1),
        (Comparison::Equal, false, sign, mask, 0),
        (Comparison::Equal, false, mask, mask, 1),
    ] {
        let mut f = function(
            ty,
            Op::Compare {
                op,
                left: 0,
                right: 1,
            },
        );
        f.returns = Some(Type::I32);
        f.blocks[0].instructions[0].result = Some((2, Type::I32));
        if invert {
            f.blocks[0].instructions.extend([
                Instruction {
                    result: Some((3, Type::I32)),
                    op: Op::Constant(0),
                    il_offset: 1,
                },
                Instruction {
                    result: Some((4, Type::I32)),
                    op: Op::Compare {
                        op: Comparison::Equal,
                        left: 2,
                        right: 3,
                    },
                    il_offset: 2,
                },
            ]);
            f.blocks[0].terminator.kind = Control::Return(Some(4));
        }
        for (f, arguments) in [
            (f.clone(), vec![a, b]),
            (control_flow_scalars::constant_inputs(f, &[a, b]), vec![]),
        ] {
            let mut optimized = f.clone();
            koh_compile::optimize::simplify(&mut optimized).unwrap();
            for f in [f, optimized] {
                cases.push((f, arguments.clone(), Ok(expected)));
            }
        }
    }
    for batch in cases.chunks(32) {
        run(host, batch);
    }
}

#[test]
fn scalar_leaf_call_passes_byte_arguments_and_full_return_buffer() {
    let host = host();
    let mut leaf = function(
        Type::I32,
        Op::Binary {
            op: Binary::Add,
            left: 0,
            right: 1,
        },
    );
    leaf.identity.token = 0x06000002;
    leaf.blocks[0].instructions.push(Instruction {
        result: Some((3, Type::I32)),
        op: Op::Convert {
            value: 2,
            bits: 8,
            signed: false,
        },
        il_offset: 1,
    });
    leaf.blocks[0].terminator.kind = Control::Return(Some(3));
    let caller = function(
        Type::I32,
        Op::Call {
            target: leaf.identity,
            signature: Signature {
                parameters: vec![Type::I32; 2],
                returns: Some(Type::I32),
            },
            arguments: vec![0, 1],
        },
    );
    let mut programs = Vec::new();
    for optimized in [false, true] {
        let mut functions = vec![caller.clone(), leaf.clone()];
        if optimized {
            for f in &mut functions {
                koh_compile::optimize::simplify(f).unwrap();
            }
        }
        for (arguments, expected) in [(vec![40, 2], 42), (vec![255, 1], 0)] {
            programs.push((
                rom_program(&functions, caller.identity, &arguments, 0xd000),
                Ok(expected),
            ));
        }
    }
    run_roms(&host, &programs, 0xd000);
}

#[rstest::rstest]
#[case(Type::Native, 1, 2)]
#[case(Type::I32, 0x01020304, 0xf1020304)]
#[case(Type::I64, 0x0102030405060708, 0x1102030405060708)]
fn parallel_loop_parameters_preserve_mixed_width_cycles(
    host: &Host,
    #[case] ty: Type,
    #[case] a: u64,
    #[case] b: u64,
) {
    let original = parallel_phis::swap(ty);
    let mut optimized = original.clone();
    koh_compile::optimize::simplify(&mut optimized).unwrap();
    let mut cases = Vec::new();
    for swaps in 0..=3 {
        for f in [&original, &optimized] {
            verify(f).unwrap();
            cases.push((
                f.clone(),
                vec![a, b, swaps],
                Ok(if swaps % 2 == 0 { a } else { b }),
            ));
        }
    }
    run(host, &cases);
}

#[rstest::rstest]
#[case(8)]
#[case(16)]
#[case(32)]
fn scalar_width_contracts_execute_before_and_after_optimization(host: &Host, #[case] bits: u16) {
    let mut programs = Vec::new();
    let cases = control_flow_scalars::cases(bits);
    assert_eq!(
        cases.len(),
        match bits {
            8 => 12,
            16 => 20,
            32 => 14,
            _ => unreachable!(),
        }
    );
    for (f, arguments, expected) in cases {
        for (f, arguments) in [
            (f.clone(), arguments.clone()),
            (control_flow_scalars::constant_inputs(f, &arguments), vec![]),
        ] {
            let mut optimized = f.clone();
            koh_compile::optimize::simplify(&mut optimized).unwrap();
            for function in [f, optimized] {
                verify(&function).unwrap();
                programs.push((function, arguments.clone(), Ok(expected)));
            }
        }
    }
    if bits == 16 {
        for (f, arguments) in [
            (control_flow_scalars::subtraction_chain(), vec![]),
            (
                control_flow_scalars::dynamic_subtraction_chain(),
                vec![200, 100, 50],
            ),
        ] {
            let mut optimized = f.clone();
            koh_compile::optimize::simplify(&mut optimized).unwrap();
            for function in [f, optimized] {
                verify(&function).unwrap();
                programs.push((function, arguments.clone(), Ok(150)));
            }
        }
    }
    for chunk in programs.chunks(32) {
        run(host, chunk);
    }
}

#[rstest::rstest]
#[case(8)]
#[case(16)]
#[case(32)]
#[case(64)]
fn comparison_branches_preserve_predicates_and_all_consumers(host: &Host, #[case] bits: u16) {
    let mut cases = Vec::new();
    for (predicate, a, b, condition) in compare_branch::inputs(bits) {
        for shape in ["single", "shared", "barrier"] {
            let function = compare_branch::function(bits, predicate, shape);
            let mut optimized = function.clone();
            koh_compile::optimize::simplify(&mut optimized).unwrap();
            if shape == "barrier" {
                assert!(
                    optimized
                        .blocks
                        .iter()
                        .flat_map(|b| &b.instructions)
                        .any(|i| matches!(i.op, Op::StoreMemory { bits: 8, .. }))
                );
            }
            let expected = match (shape, condition) {
                ("shared", true) => 11,
                ("shared", false) => 99,
                (_, true) => 111,
                (_, false) => 222,
            };
            for f in [function, optimized] {
                verify(&f).unwrap();
                let object = sm83::emit(&f).unwrap();
                assert!(object.sections.iter().map(|s| s.data.len()).sum::<usize>() <= 2048);
                cases.push((f, vec![a, b], Ok(expected)));
            }
        }
    }
    for (a, b, expected) in [(1, 2, 1), (2, 1, 0), (42, 42, 0)] {
        let function = compare_branch::function(bits, Comparison::Equal, "mixed");
        let mut optimized = function.clone();
        koh_compile::optimize::simplify(&mut optimized).unwrap();
        for f in [function, optimized] {
            cases.push((f, vec![a, b], Ok(expected)));
        }
    }
    run(host, &cases);
    let directory = tempfile::tempdir_in(host.directory.path()).unwrap();
    for (shape, a, b, expected) in [
        ("single", 1, 2, 111u64),
        ("shared", 42, 42, 11),
        ("mixed", 1, 2, 1),
    ] {
        let predicate = if shape == "single" {
            Comparison::LessUnsigned
        } else {
            Comparison::Equal
        };
        let f = compare_branch::function(bits, predicate, shape);
        let path = directory.path().join("compare.gb");
        std::fs::write(&path, rom(&f, &[a, b], 0xd000)).unwrap();
        let output = dotnet()
            .arg(&host.dll)
            .arg("--cost")
            .arg(path)
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
        let output = String::from_utf8(output.stdout).unwrap();
        let fields: Vec<_> = output.split_whitespace().collect();
        assert_eq!(fields[0], "A5", "{output}");
        assert_eq!(
            fields[2],
            expected
                .to_le_bytes()
                .iter()
                .map(|b| format!("{b:02X}"))
                .collect::<String>(),
            "{output}"
        );
        assert_eq!(&fields[3..5], &["D000", "OK"], "{output}");
        let cycles: u64 = fields[5].parse().unwrap();
        println!("{bits}/{shape}: {cycles} T-cycles");
        assert!(cycles < 70224, "{bits}/{shape}: {cycles} T-cycles");
    }
}

#[rstest::rstest]
#[case(8)]
#[case(16)]
#[case(32)]
#[case(64)]
fn comparison_chain_optimization_preserves_boolean_and_integer_semantics(
    host: &Host,
    #[case] bits: u16,
    #[values("double-negation", "ne-zero", "eq-zero", "eq-one", "nonboolean")] shape: &str,
) {
    let function = optimizer_contracts::comparison(bits, shape);
    verify(&function).unwrap();
    let mut optimized = function.clone();
    koh_compile::optimize::simplify(&mut optimized).unwrap();
    verify(&optimized).unwrap();
    let mut cases = Vec::new();
    for (a, b) in optimizer_contracts::comparison_pairs(bits) {
        let expected = optimizer_contracts::comparison_expected(bits, shape, a, b);
        for f in [&function, &optimized] {
            cases.push((f.clone(), vec![a, b], Ok(expected)));
        }
    }
    run(host, &cases);
}

#[rstest::rstest]
#[case(8)]
#[case(16)]
#[case(32)]
fn conversion_chain_optimization_preserves_bits_and_wraparound(
    host: &Host,
    #[case] bits: u16,
    #[values(
        "zero-roundtrip",
        "sign-roundtrip",
        "different-width",
        "reverse",
        "increment"
    )]
    shape: &str,
) {
    let function = optimizer_contracts::conversion(bits, shape);
    verify(&function).unwrap();
    let mut optimized = function.clone();
    koh_compile::optimize::simplify(&mut optimized).unwrap();
    verify(&optimized).unwrap();
    let mut cases = Vec::new();
    for value in optimizer_contracts::CONVERSION_VALUES {
        let expected = optimizer_contracts::conversion_expected(bits, shape, value);
        for f in [&function, &optimized] {
            cases.push((f.clone(), vec![value], Ok(expected)));
        }
    }
    run(host, &cases);
}

#[rstest::rstest]
#[case(0xd6u8)]
#[case(0xd7)]
#[case(0xd8)]
#[case(0xd9)]
#[case(0xda)]
#[case(0xdb)]
fn checked_cil_arithmetic_matches_wide_integer_oracles(
    host: &Host,
    #[case] opcode: u8,
    #[values((Type::Native,Type::Native),(Type::I32,Type::I32),(Type::I64,Type::I64),(Type::Native,Type::I32),(Type::I32,Type::Native))]
    inputs: (Type, Type),
) {
    let returns = if inputs.0 == Type::Native || inputs.1 == Type::Native {
        Type::Native
    } else {
        inputs.0
    };
    let function = koh_compile::lower::lower(koh_compile::lower::Method {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        il: &[0x02, 0x03, opcode, 0x2a],
        parameters: vec![inputs.0, inputs.1],
        returns: Some(returns),
        locals: vec![],
        init_locals: true,
        max_stack: 2,
    })
    .unwrap();
    let mut optimized = function.clone();
    koh_compile::optimize::simplify(&mut optimized).unwrap();
    let mask = |ty: Type| u64::MAX >> (64 - ty.bits());
    let sign = |ty: Type| 1u64 << (ty.bits() - 1);
    let unsigned = matches!(opcode, 0xd7 | 0xd9 | 0xdb);
    let number = |raw: u64, ty: Type| {
        let raw = raw & mask(ty);
        if !unsigned && raw & sign(ty) != 0 {
            raw as i128 - (1i128 << ty.bits())
        } else {
            raw as i128
        }
    };
    let min = if unsigned {
        0
    } else {
        -(1i128 << (returns.bits() - 1))
    };
    let max = (1i128 << (returns.bits() - u16::from(!unsigned))) - 1;
    let mut cases = Vec::new();
    let mut pairs = vec![
        (0, 0),
        (1, 2),
        (mask(inputs.0), 1),
        (sign(inputs.0) - 1, 1),
        (sign(inputs.0), mask(inputs.1)),
        (0, mask(inputs.1)),
        (mask(inputs.0), mask(inputs.1)),
        (sign(inputs.0), sign(inputs.1)),
        (0, 65536 & mask(inputs.1)),
        (1, 65535 & mask(inputs.1)),
        (0, sign(inputs.1) - 1),
        (65536 & mask(inputs.0), 0),
        (65535 & mask(inputs.0), 1),
    ];
    if matches!(opcode, 0xd8 | 0xd9) {
        let root = (max as u128).isqrt() as u64;
        pairs.extend([
            (root, root),
            (root + 1, root + 1),
            (root.wrapping_neg() & mask(inputs.0), root),
            (
                root.wrapping_add(1).wrapping_neg() & mask(inputs.0),
                root + 1,
            ),
        ]);
        if returns == Type::I64 {
            if unsigned {
                pairs.extend([(1 << 32, (1 << 32) - 1), (u64::MAX - 1, 0x1ffffffff)]);
            } else {
                pairs.extend([
                    (1 << 32, (1 << 31) - 1),
                    ((1u64 << 32).wrapping_neg(), 1 << 31),
                    ((1u64 << 32).wrapping_neg(), (1 << 31) + 1),
                ]);
            }
        }
    }
    for (left, right) in pairs {
        let expected = if opcode == 0xd9 {
            let product = (number(left, inputs.0) as u128) * (number(right, inputs.1) as u128);
            if product <= max as u128 {
                Ok(product as u64 & mask(returns))
            } else {
                Err(2)
            }
        } else {
            let value = match opcode {
                0xd8 => number(left, inputs.0) * number(right, inputs.1),
                0xda | 0xdb => number(left, inputs.0) - number(right, inputs.1),
                _ => number(left, inputs.0) + number(right, inputs.1),
            };
            if (min..=max).contains(&value) {
                Ok(value as u64 & mask(returns))
            } else {
                Err(2)
            }
        };
        for f in [&function, &optimized] {
            cases.push((f.clone(), vec![left, right], expected));
        }
    }
    run(host, &cases);
}

#[rstest::rstest]
#[case(Type::Native, "entry")]
#[case(Type::I32, "entry")]
#[case(Type::I64, "entry")]
#[case(Type::Native, "branch")]
#[case(Type::I32, "branch")]
#[case(Type::I64, "branch")]
#[case(Type::Native, "loop")]
#[case(Type::I32, "loop")]
#[case(Type::I64, "loop")]
fn live_values_survive_entry_branches_and_backedges(
    host: &Host,
    #[case] ty: Type,
    #[case] shape: &str,
) {
    let mask = u64::MAX >> (64 - ty.bits());
    let sign = 1u64 << (ty.bits() - 1);
    let mut cases = Vec::new();
    let mut add = |function: Function, arguments: Vec<u64>, expected: u64| {
        verify(&function).unwrap();
        let mut optimized = function.clone();
        koh_compile::optimize::simplify(&mut optimized).unwrap();
        verify(&optimized).unwrap();
        cases.push((function, arguments.clone(), Ok(expected)));
        cases.push((optimized, arguments, Ok(expected)));
    };
    match shape {
        "entry" => {
            for constant in [1, 7] {
                for n in [0, 1, 127, 0xffff, sign, mask] {
                    add(
                        liveness::entry(ty, constant),
                        vec![n],
                        n.wrapping_mul(2).wrapping_add(constant) & mask,
                    );
                }
            }
        }
        "branch" => {
            for condition in [0, 0x8000_0000] {
                for n in [0, 1, 127, 0xffff, sign, mask] {
                    add(
                        liveness::branch(ty),
                        vec![condition, n],
                        n.wrapping_add(if condition == 0 { 3 } else { 2 }) & mask,
                    );
                }
            }
        }
        "loop" => {
            for n in [0, 1, 2, 10, 64, sign, mask] {
                add(
                    liveness::loop_count(ty),
                    vec![n],
                    if n & sign == 0 { n } else { 0 },
                );
            }
        }
        _ => unreachable!(),
    }
    assert_eq!(cases.len(), if shape == "loop" { 14 } else { 24 });
    run(host, &cases);
}

#[rstest::rstest]
#[case(Type::Native)]
#[case(Type::I32)]
#[case(Type::I64)]
fn constructed_sum_loop_executes_before_and_after_optimization(host: &Host, #[case] ty: Type) {
    let original = sum_loop::sum(ty);
    let mut optimized = original.clone();
    koh_compile::optimize::simplify(&mut optimized).unwrap();
    let mut cases = Vec::new();
    for n in [0u64, 1, 2, 10, 257, 400] {
        let expected = (n * n.saturating_sub(1) / 2) & (u64::MAX >> (64 - ty.bits()));
        for function in [&original, &optimized] {
            cases.push((function.clone(), vec![n], Ok(expected)));
        }
    }
    run(host, &cases);
}

#[fixture]
#[once]
fn host() -> Host {
    support::build_host()
}

#[rstest::rstest]
fn recursive_signed_widening_constant_executes_without_type_erasure(host: &Host) {
    use recursion_fixture::{instruction, sum};
    let mut function = sum(Type::I64, 64, false);
    function.blocks[2]
        .instructions
        .insert(4, instruction(11, Type::I32, Op::Constant(0xffff_ffff)));
    function.blocks[2].instructions.insert(
        5,
        instruction(
            12,
            Type::I64,
            Op::Convert {
                value: 11,
                bits: 64,
                signed: true,
            },
        ),
    );
    function.blocks[2].instructions[6].op = Op::Binary {
        op: Binary::Add,
        left: 8,
        right: 12,
    };
    koh_compile::optimize::simplify(&mut function).unwrap();
    run(
        host,
        &[
            (function.clone(), vec![0, 0], Ok(0)),
            (function.clone(), vec![1, 0], Ok(u64::MAX)),
            (function, vec![3, 0], Ok(u64::MAX - 2)),
        ],
    );
}

#[rstest::rstest]
#[case(Type::I32, 8, false, 200)]
#[case(Type::I32, 8, true, 200)]
#[case(Type::I32, 16, false, 40000)]
#[case(Type::I32, 16, true, 65533)]
#[case(Type::I32, 32, false, 0xffff_fffd)]
#[case(Type::I64, 64, false, 0x8000_0000_0000_0001)]
fn recursive_and_accumulator_roms_match_modular_arithmetic(
    host: &Host,
    #[case] ty: Type,
    #[case] bits: u8,
    #[case] signed: bool,
    #[case] term: u64,
) {
    let original = recursion_fixture::sum(ty, bits, signed);
    let mut optimized = original.clone();
    koh_compile::optimize::simplify(&mut optimized).unwrap();
    let mask = |bits| {
        if bits == 64 {
            u64::MAX
        } else {
            (1u64 << bits) - 1
        }
    };
    let mut cases = Vec::new();
    for n in [0u64, 17, 500] {
        let mut expected = n.wrapping_mul(term) & mask(bits);
        if signed && expected & (1u64 << (bits - 1)) != 0 {
            expected |= mask(ty.bits() as u8) ^ mask(bits);
        }
        cases.push((optimized.clone(), vec![n, term], Ok(expected)));
        if n < 500 {
            cases.push((original.clone(), vec![n, term], Ok(expected)));
        }
    }
    run(host, &cases);
}

#[rstest::rstest]
#[case(&[0x15,0xd3,0x20,255,0,0,0,0x5f,0x2a],0xff)]
#[case(&[0x20,255,0,0,0,0x15,0xd3,0x5f,0x2a],0xff)]
#[case(&[0x17,0xd3,0x1f,0x10,0x60,0x2a],0x11)]
#[case(&[0x17,0x1f,0x10,0xd3,0x61,0x2a],0x11)]
#[case(&[0x19,0x18,0xd3,0x5a,0x2a],6)]
#[case(&[0x1f,0xf9,0x18,0xd3,0x5b,0x2a],0xfffd)]
#[case(&[0x1f,0xf9,0x18,0xd3,0x5d,0x2a],0xffff)]
#[case(&[0x1d,0x18,0xd3,0x5c,0x2a],3)]
#[case(&[0x1d,0x18,0xd3,0x5e,0x2a],1)]
#[case(&[0x17,0xd3,0x18,0xd3,0x62,0x2a],4)]
fn mixed_native_integer_operations_obey_the_operand_table(
    host: &Host,
    #[case] il: &[u8],
    #[case] expected: u64,
) {
    let f = koh_compile::lower::lower(koh_compile::lower::Method {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        il,
        parameters: vec![],
        returns: Some(Type::Native),
        locals: vec![],
        init_locals: true,
        max_stack: 2,
    })
    .unwrap();
    run(host, &[(f, vec![], Ok(expected))]);
}

#[rstest::rstest]
#[case(0)]
#[case(20)]
fn hblank_stream_truncates_bytes_and_preserves_large_frames(host: &Host, #[case] locals: usize) {
    let mut f = function(Type::I32, Op::Constant(0));
    f.parameters = vec![Type::Native, Type::I32, Type::I32];
    f.locals = vec![Type::I64; locals];
    f.blocks[0].parameters = vec![(0, Type::Native), (1, Type::I32), (2, Type::I32)];
    f.blocks[0].instructions.clear();
    for (id, ty, value) in [
        (3, Type::Native, 0xff40),
        (4, Type::I32, 0x91),
        (5, Type::Native, 0xff43),
        (6, Type::I32, 0x33),
        (7, Type::I32, 0),
    ] {
        f.blocks[0].instructions.push(Instruction {
            result: Some((id, ty)),
            op: Op::Constant(value),
            il_offset: 0,
        });
    }
    for (address, value) in [(3, 4), (5, 7), (0, 6)] {
        f.blocks[0].instructions.push(Instruction {
            result: None,
            op: Op::StoreMemory {
                address,
                value,
                bits: 8,
            },
            il_offset: 0,
        });
    }
    f.blocks[0].instructions.push(Instruction {
        result: None,
        op: Op::HBlankStream {
            source: 0,
            first: 1,
            count: 2,
            register: 0xff43,
        },
        il_offset: 1,
    });
    f.blocks[0].instructions.push(Instruction {
        result: Some((8, Type::I32)),
        op: Op::LoadMemory {
            address: 5,
            bits: 8,
            signed: false,
        },
        il_offset: 2,
    });
    f.blocks[0].terminator.kind = Control::Return(Some(8));
    let cases = [
        (0x100, 0x100, Ok(0)),
        (0x100, 0x101, Ok(0x33)),
        (0x18f, 1, Ok(0x33)),
        (0x1ff, 0, Ok(0)),
        (0x1ff, 1, Err(6)),
        (143, 0x102, Err(6)),
    ]
    .map(|(first, count, result)| (f.clone(), vec![0xc018, first, count], result));
    run(host, &cases);
    run_at(host, &[(f, vec![0xc018, 0, 1], Err(1))], 0xc120);
}

fn function(ty: Type, op: Op) -> Function {
    Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![ty, ty],
        returns: Some(ty),
        locals: vec![],
        blocks: vec![Block {
            parameters: vec![(0, ty), (1, ty)],
            instructions: vec![Instruction {
                result: Some((2, ty)),
                op,
                il_offset: 0,
            }],
            terminator: Terminator {
                kind: Control::Return(Some(2)),
                il_offset: 1,
            },
        }],
    }
}

#[rstest::rstest]
#[case(5)]
#[case(16)]
#[case(64)]
fn aggregate_values_copy_initialize_and_cross_calls(host: &Host, #[case] bytes: u16) {
    let shape = ValueType::new(
        Definition {
            assembly: 0,
            token: 0x02000001,
        },
        bytes,
    )
    .unwrap();
    let ty = Type::Struct(shape);
    let child = ValueType::new(
        Definition {
            assembly: 0,
            token: 0x02000002,
        },
        bytes / 2,
    )
    .unwrap();
    let scalar = if bytes % 8 == 0 {
        Scalar::U64
    } else {
        Scalar::U8
    };
    let storage = Storage::Scalar(scalar);
    let scalar_ty = storage.stack_type();
    let step = storage.bytes();
    let pattern = |offset: u16| {
        if step == 8 {
            0x0123456789abcdefu64.rotate_left(u32::from(offset))
        } else {
            u64::from(0xa0 + offset)
        }
    };
    let mut code = vec![];
    let mut next = 2u32;
    let mut value = |code: &mut Vec<Instruction>, ty, op| {
        let id = next;
        next += 1;
        code.push(Instruction {
            result: Some((id, ty)),
            op,
            il_offset: id,
        });
        id
    };
    let store = |code: &mut Vec<Instruction>, op| {
        code.push(Instruction {
            result: None,
            op,
            il_offset: 0,
        })
    };
    let zero = value(&mut code, ty, Op::ZeroStruct);
    store(
        &mut code,
        Op::StoreLocal {
            local: 0,
            value: zero,
        },
    );
    let local = value(
        &mut code,
        Type::StructRef(shape),
        Op::LocalAddress {
            local: 0,
            storage: FieldStorage::Value(Storage::Struct(shape)),
        },
    );
    let mut valid = value(&mut code, Type::I32, Op::Constant(1));
    for offset in (0..bytes).step_by(usize::from(step)) {
        let address = value(
            &mut code,
            storage.reference_type(),
            Op::FieldAddress {
                address: local,
                owner: shape,
                offset,
                storage: FieldStorage::Value(storage),
            },
        );
        let actual = value(
            &mut code,
            scalar_ty,
            Op::LoadMemory {
                address,
                bits: step * 8,
                signed: false,
            },
        );
        let expected = value(&mut code, scalar_ty, Op::Constant(0));
        let equal = value(
            &mut code,
            Type::I32,
            Op::Compare {
                op: Comparison::Equal,
                left: actual,
                right: expected,
            },
        );
        valid = value(
            &mut code,
            Type::I32,
            Op::Binary {
                op: Binary::And,
                left: valid,
                right: equal,
            },
        );
        let address = value(
            &mut code,
            Type::Native,
            Op::FieldAddress {
                address: 0,
                owner: shape,
                offset,
                storage: FieldStorage::Value(storage),
            },
        );
        let data = value(&mut code, scalar_ty, Op::Constant(pattern(offset)));
        store(
            &mut code,
            Op::StoreMemory {
                address,
                value: data,
                bits: step * 8,
            },
        );
    }
    let snapshot = value(
        &mut code,
        ty,
        Op::LoadMemory {
            address: 0,
            bits: bytes * 8,
            signed: false,
        },
    );
    let changed = value(&mut code, scalar_ty, Op::Constant(0));
    store(
        &mut code,
        Op::StoreMemory {
            address: 0,
            value: changed,
            bits: step * 8,
        },
    );
    let callee = Definition {
        assembly: 0,
        token: 0x06000002,
    };
    let returned = value(
        &mut code,
        ty,
        Op::Call {
            target: callee,
            signature: Signature {
                parameters: vec![ty],
                returns: Some(ty),
            },
            arguments: vec![snapshot],
        },
    );
    store(
        &mut code,
        Op::StoreLocal {
            local: 0,
            value: returned,
        },
    );
    let copied = value(&mut code, ty, Op::LoadLocal(0));
    store(
        &mut code,
        Op::StoreMemory {
            address: 1,
            value: copied,
            bits: bytes * 8,
        },
    );
    let nested = value(
        &mut code,
        Type::Struct(child),
        Op::ExtractField {
            value: returned,
            offset: bytes - child.bytes(),
            storage: FieldStorage::Value(Storage::Struct(child)),
        },
    );
    let address = value(
        &mut code,
        Type::Native,
        Op::FieldAddress {
            address: 1,
            owner: shape,
            offset: bytes - child.bytes(),
            storage: FieldStorage::Value(Storage::Struct(child)),
        },
    );
    store(
        &mut code,
        Op::StoreMemory {
            address,
            value: nested,
            bits: child.bytes() * 8,
        },
    );
    let signed = value(
        &mut code,
        Type::I32,
        Op::ExtractField {
            value: returned,
            offset: 0,
            storage: FieldStorage::Value(Storage::Scalar(Scalar::I8)),
        },
    );
    let expected = value(
        &mut code,
        Type::I32,
        Op::Constant(u64::from(pattern(0) as u8 as i8 as i32 as u32)),
    );
    let equal = value(
        &mut code,
        Type::I32,
        Op::Compare {
            op: Comparison::Equal,
            left: signed,
            right: expected,
        },
    );
    valid = value(
        &mut code,
        Type::I32,
        Op::Binary {
            op: Binary::And,
            left: valid,
            right: equal,
        },
    );
    for offset in (0..bytes).step_by(usize::from(step)) {
        let address = value(
            &mut code,
            Type::Native,
            Op::FieldAddress {
                address: 1,
                owner: shape,
                offset,
                storage: FieldStorage::Value(storage),
            },
        );
        let actual = value(
            &mut code,
            scalar_ty,
            Op::LoadMemory {
                address,
                bits: step * 8,
                signed: false,
            },
        );
        let expected = value(&mut code, scalar_ty, Op::Constant(pattern(offset)));
        let equal = value(
            &mut code,
            Type::I32,
            Op::Compare {
                op: Comparison::Equal,
                left: actual,
                right: expected,
            },
        );
        valid = value(
            &mut code,
            Type::I32,
            Op::Binary {
                op: Binary::And,
                left: valid,
                right: equal,
            },
        );
    }
    let result = value(
        &mut code,
        Type::I64,
        Op::Convert {
            value: valid,
            bits: 32,
            signed: false,
        },
    );
    let root = Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![Type::Native, Type::Native],
        returns: Some(Type::I64),
        locals: vec![ty],
        blocks: vec![Block {
            parameters: vec![(0, Type::Native), (1, Type::Native)],
            instructions: code,
            terminator: Terminator {
                kind: Control::Return(Some(result)),
                il_offset: 0,
            },
        }],
    };
    let echo = Function {
        identity: callee,
        parameters: vec![ty],
        returns: Some(ty),
        locals: vec![],
        blocks: vec![Block {
            parameters: vec![(0, ty)],
            instructions: vec![],
            terminator: Terminator {
                kind: Control::Return(Some(0)),
                il_offset: 0,
            },
        }],
    };
    let rom = rom_program(
        &[root.clone(), echo],
        root.identity,
        &[0xd000, 0xd080],
        0xd000,
    );
    run_roms(host, &[(rom, Ok(1))], 0xd000);
}

#[rstest::rstest]
#[case(0)]
#[case(8)]
fn null_struct_field_reference_traps_before_offset(host: &Host, #[case] offset: u16) {
    let shape = ValueType::new(
        Definition {
            assembly: 0,
            token: 0x02000001,
        },
        64,
    )
    .unwrap();
    let f = Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![Type::StructRef(shape)],
        returns: Some(Type::I32),
        locals: vec![],
        blocks: vec![Block {
            parameters: vec![(0, Type::StructRef(shape))],
            instructions: vec![
                Instruction {
                    result: Some((1, Type::ByRef(Scalar::U32))),
                    op: Op::FieldAddress {
                        address: 0,
                        owner: shape,
                        offset,
                        storage: FieldStorage::Value(Storage::Scalar(Scalar::U32)),
                    },
                    il_offset: 0,
                },
                Instruction {
                    result: Some((2, Type::I32)),
                    op: Op::LoadMemory {
                        address: 1,
                        bits: 32,
                        signed: false,
                    },
                    il_offset: 1,
                },
            ],
            terminator: Terminator {
                kind: Control::Return(Some(2)),
                il_offset: 2,
            },
        }],
    };
    run(host, &[(f, vec![0], Err(4))]);
}

fn symbol(name: String, definition: SymbolDefinition) -> Symbol {
    Symbol {
        name,
        binding: if definition == SymbolDefinition::Import {
            SymbolBinding::Local
        } else {
            SymbolBinding::Exported
        },
        definition,
        source: None,
    }
}

fn patch(offset: u32, target: SymbolId) -> Patch {
    Patch {
        offset: offset as usize,
        width: PatchWidth::Word,
        semantics: PatchSemantics::Absolute,
        pc: AddressContext {
            section: SectionId(0),
            offset,
        },
        expression: RelocationExpr::Symbol(target),
        source: Provenance {
            file: "<test-start>".into(),
            line: 1,
            column: 1,
            expansion: vec![],
        },
    }
}

fn rom(f: &Function, arguments: &[u64], stack: u16) -> Vec<u8> {
    rom_program(std::slice::from_ref(f), f.identity, arguments, stack)
}

fn rom_program(
    functions: &[Function],
    entry: Definition,
    arguments: &[u64],
    stack: u16,
) -> Vec<u8> {
    rom_program_data(functions, entry, arguments, stack, &[])
}

fn rom_program_data(
    functions: &[Function],
    entry: Definition,
    arguments: &[u64],
    stack: u16,
    data_objects: &[Object],
) -> Vec<u8> {
    verify_program(functions).unwrap();
    let f = functions.iter().find(|f| f.identity == entry).unwrap();
    let objects: Vec<_> = functions
        .iter()
        .map(|f| read_elf(&write_elf(&sm83::emit(f).unwrap()).unwrap()).unwrap())
        .collect();
    let mut data = vec![0; 0x150];
    if !data_objects.is_empty() {
        data[0x147] = 0x19;
    }
    data[0x100..0x103].copy_from_slice(&[0xc3, 0x50, 0x01]);
    data.extend_from_slice(&[0xf3, 0x31, stack as u8, (stack >> 8) as u8]);
    let ret_bytes = f.returns.map(|t| t.bits() / 8).unwrap_or(0);
    let bytes = usize::from(ret_bytes)
        + f.parameters
            .iter()
            .map(|t| usize::from(t.bits() / 8))
            .sum::<usize>();
    assert!(bytes < 128);
    data.extend_from_slice(&[0xe8, (-(bytes as i16)) as u8]);
    let mut offset = usize::from(ret_bytes);
    for (&argument, ty) in arguments.iter().zip(&f.parameters) {
        for byte in argument
            .to_le_bytes()
            .iter()
            .take(usize::from(ty.bits() / 8))
        {
            data.extend_from_slice(&[0xf8, offset as u8, 0x36, *byte]);
            offset += 1;
        }
    }
    data.push(0xcd);
    let call = data.len() as u32;
    data.extend_from_slice(&[0, 0]);
    for i in 0..ret_bytes {
        let i = u8::try_from(i).unwrap();
        data.extend_from_slice(&[0xf8, i, 0x7e, 0xea, 0x08 + i, 0xc0]);
    }
    data.extend_from_slice(&[0xe8, bytes as u8, 0x3e, 0xa5, 0xea, 0x00, 0xc0, 0x18, 0xfe]);
    let stack_trap = data.len() as u32;
    data.extend_from_slice(&[
        0x3e, 1, 0xea, 1, 0xc0, 0x3e, 0xee, 0xea, 0, 0xc0, 0x18, 0xfe,
    ]);
    let arithmetic_trap = data.len() as u32;
    data.extend_from_slice(&[
        0x3e, 2, 0xea, 1, 0xc0, 0x3e, 0xee, 0xea, 0, 0xc0, 0x18, 0xfe,
    ]);
    let null_trap = data.len() as u32;
    data.extend_from_slice(&[
        0x3e, 4, 0xea, 1, 0xc0, 0x3e, 0xee, 0xea, 0, 0xc0, 0x18, 0xfe,
    ]);
    let pin_trap = data.len() as u32;
    data.extend_from_slice(&[
        0x3e, 8, 0xea, 1, 0xc0, 0x3e, 0xee, 0xea, 0, 0xc0, 0x18, 0xfe,
    ]);
    let bounds_trap = data.len() as u32;
    data.extend_from_slice(&[
        0x3e, 6, 0xea, 1, 0xc0, 0x3e, 0xee, 0xea, 0, 0xc0, 0x18, 0xfe,
    ]);
    let hardware_trap = data.len() as u32;
    data.extend_from_slice(&[
        0x3e, 9, 0xea, 1, 0xc0, 0x3e, 0xee, 0xea, 0, 0xc0, 0x18, 0xfe,
    ]);
    data.resize(0x300, 0);
    let entry = Object {
        sections: vec![Section {
            name: "test.start".into(),
            kind: 0,
            address: Some(0),
            bank: Some(0),
            size: data.len() as u32,
            data,
            ..Default::default()
        }],
        symbols: vec![
            symbol(sm83::symbol(f.identity), SymbolDefinition::Import),
            symbol(
                "__koh_trap_stack".into(),
                SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: stack_trap,
                },
            ),
            symbol(
                "__koh_trap_arithmetic".into(),
                SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: arithmetic_trap,
                },
            ),
            symbol(
                "__koh_trap_null".into(),
                SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: null_trap,
                },
            ),
            symbol(
                "__koh_trap_pin".into(),
                SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: pin_trap,
                },
            ),
            symbol(
                "__koh_trap_bounds".into(),
                SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: bounds_trap,
                },
            ),
            symbol(
                "__koh_trap_hardware".into(),
                SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: hardware_trap,
                },
            ),
        ],
    };
    let mut entry = entry;
    entry.sections[0].patches.push(patch(call, SymbolId(0)));
    let mut all = vec![entry];
    all.extend(objects);
    all.extend_from_slice(data_objects);
    koh_link::link(&all, &koh_link::LinkOptions::default())
        .unwrap()
        .rom
}

fn run(host: &Host, cases: &[(Function, Vec<u64>, Result<u64, u8>)]) {
    run_at(host, cases, 0xd000);
}

fn run_at(host: &Host, cases: &[(Function, Vec<u64>, Result<u64, u8>)], stack: u16) {
    let programs: Vec<_> = cases
        .iter()
        .map(|(f, args, expected)| (rom(f, args, stack), *expected))
        .collect();
    run_roms(host, &programs, stack);
}

fn run_roms(host: &Host, cases: &[(Vec<u8>, Result<u64, u8>)], stack: u16) {
    run_roms_observed(host, cases, stack, None)
}

fn run_roms_observed(
    host: &Host,
    cases: &[(Vec<u8>, Result<u64, u8>)],
    stack: u16,
    observed: Option<&[String]>,
) {
    if let Some(observed) = observed {
        assert_eq!(observed.len(), cases.len());
    }
    let directory = tempfile::Builder::new()
        .prefix("rom run ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let mut command = dotnet();
    command.arg(&host.dll);
    if observed.is_some() {
        command.arg("--observe-memory");
    }
    for (index, (rom, _)) in cases.iter().enumerate() {
        let path = directory.path().join(format!("case{index}.gb"));
        std::fs::write(&path, rom).unwrap();
        command.arg(path);
    }
    let output = command.output().unwrap();
    assert!(
        output.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let stdout = String::from_utf8(output.stdout).unwrap();
    let lines: Vec<_> = stdout.lines().collect();
    assert_eq!(lines.len(), cases.len(), "{stdout}");
    for (index, (line, (_, expected))) in lines.iter().zip(cases).enumerate() {
        let fields: Vec<_> = line.split_whitespace().collect();
        if let Some(observed) = observed {
            assert_eq!(
                fields.get(5),
                Some(&observed[index].as_str()),
                "case {index}: {line}"
            );
        }
        assert_eq!(
            fields[4], "OK",
            "stack guard modified reserved memory in case {index}"
        );
        if let Err(fault) = expected {
            assert_eq!(fields[0], "EE", "case {index}: {line}");
            assert_eq!(fields[1], format!("{fault:02X}"), "case {index}: {line}");
            continue;
        }
        assert_eq!(fields[0], "A5", "case {index}: {line}");
        let result = (0..8)
            .map(|i| {
                u64::from(u8::from_str_radix(&fields[2][i * 2..i * 2 + 2], 16).unwrap()) << (i * 8)
            })
            .fold(0, |a, b| a | b);
        assert_eq!(Ok(result), *expected, "case {index}: {line}");
        assert_eq!(
            fields[3],
            format!("{stack:04X}"),
            "unbalanced SP in case {index}"
        );
    }
}

#[rstest::rstest]
fn managed_calls_preserve_caller_values_and_guard_call_storage(host: &Host) {
    let mut callee = function(
        Type::I32,
        Op::Binary {
            op: Binary::Add,
            left: 0,
            right: 1,
        },
    );
    callee.identity.token += 1;
    let mut caller = function(
        Type::I32,
        Op::Call {
            target: callee.identity,
            signature: Signature {
                parameters: vec![Type::I32; 2],
                returns: Some(Type::I32),
            },
            arguments: vec![0, 1],
        },
    );
    caller.blocks[0].instructions.push(Instruction {
        result: Some((3, Type::I32)),
        op: Op::Binary {
            op: Binary::Add,
            left: 0,
            right: 2,
        },
        il_offset: 2,
    });
    caller.blocks[0].terminator.kind = Control::Return(Some(3));
    let entry = caller.identity;
    let functions = vec![caller, callee];
    let normal = rom_program(&functions, entry, &[11, 22], 0xd000);
    run_roms(host, &[(normal, Ok(44))], 0xd000);
    let guarded = rom_program(&functions, entry, &[11, 22], 0xc144);
    run_roms(host, &[(guarded, Err(1))], 0xc144);
}

#[test]
fn stack_reservation_is_canonical_and_blocks_overlapping_globals() {
    let f = function(
        Type::I32,
        Op::Binary {
            op: Binary::Add,
            left: 0,
            right: 1,
        },
    );
    let object = read_elf(&write_elf(&sm83::emit(&f).unwrap()).unwrap()).unwrap();
    assert!(object.sections.iter().any(|s| s.kind == 4
        && s.address == Some(0xc100)
        && s.size == 0xf00
        && s.combination == SectionCombination::Union));
    let globals = Object {
        sections: vec![Section {
            name: "test.globals".into(),
            kind: 4,
            address: Some(0xc080),
            bank: Some(0),
            size: 0x90,
            ..Default::default()
        }],
        symbols: vec![],
    };
    assert!(
        koh_link::link(&[object, globals], &koh_link::LinkOptions::default())
            .unwrap_err()
            .contains("Cannot place")
    );
}

#[rstest::rstest]
fn integer_arithmetic_executes_in_existing_emulator(host: &Host) {
    let mut cases = Vec::new();
    for (ty, mask, sign) in [
        (Type::I32, 0xffff_ffff, 0x8000_0000),
        (Type::I64, u64::MAX, 0x8000_0000_0000_0000),
        (Type::Native, 0xffff, 0x8000),
    ] {
        for (op, left, right, expected) in [
            (Binary::Add, mask, 1, 0),
            (Binary::Subtract, 0, 1, mask),
            (Binary::And, mask, 0x55, 0x55),
            (Binary::Or, sign, 0x55, sign | 0x55),
            (Binary::Xor, mask, 0x55, mask ^ 0x55),
            (Binary::Multiply, mask, 3, mask - 2),
            (Binary::DivideUnsigned, mask, 2, mask >> 1),
            (Binary::DivideSigned, mask - 6, 3, mask - 1),
            (Binary::RemainderUnsigned, mask, 2, 1),
            (Binary::RemainderSigned, mask - 6, 3, mask),
        ] {
            cases.push((
                function(
                    ty,
                    Op::Binary {
                        op,
                        left: 0,
                        right: 1,
                    },
                ),
                vec![left, right],
                Ok(expected),
            ));
        }
    }
    run(host, &cases);
}

#[rstest::rstest]
#[case(Type::Native, 0)]
#[case(Type::Native, 20)]
#[case(Type::I32, 0)]
#[case(Type::I32, 20)]
#[case(Type::I64, 0)]
#[case(Type::I64, 20)]
fn narrow_arithmetic_preserves_carry_at_large_stack_offsets(
    host: &Host,
    #[case] ty: Type,
    #[case] locals: usize,
) {
    let mask = if ty == Type::I64 {
        u64::MAX
    } else {
        (1u64 << ty.bits()) - 1
    };
    let left = 254u64;
    let right = 0x123456789abcdef & mask;
    let mut cases = Vec::new();
    for (op, expected) in [
        (Binary::Add, left.wrapping_add(right)),
        (Binary::Subtract, left.wrapping_sub(right)),
        (Binary::And, left & right),
        (Binary::Or, left | right),
        (Binary::Xor, left ^ right),
    ] {
        let mut f = function(
            ty,
            Op::Binary {
                op,
                left: 3,
                right: 1,
            },
        );
        f.locals = vec![Type::I64; locals];
        f.blocks[0].instructions.insert(
            0,
            Instruction {
                result: Some((3, ty)),
                op: Op::Convert {
                    value: 0,
                    bits: 8,
                    signed: false,
                },
                il_offset: 0,
            },
        );
        cases.push((f, vec![0x12fe, right], Ok(expected & mask)));
    }
    run(host, &cases);
}

#[rstest::rstest]
#[case(Type::I32, 0)]
#[case(Type::I32, 80)]
#[case(Type::I64, 0)]
#[case(Type::I64, 80)]
fn literal_alu_preserves_carry_borrow_and_large_frames(
    host: &Host,
    #[case] ty: Type,
    #[case] locals: usize,
) {
    let mask = if ty == Type::I64 {
        u64::MAX
    } else {
        u32::MAX as u64
    };
    let mut cases = Vec::new();
    for literal in [0, 1, 0x80, 0xff, mask] {
        for left in [
            0,
            1,
            0x7f,
            0xff,
            0xffff,
            0xffffffff,
            0x8000000000000000,
            u64::MAX,
        ] {
            let left = left & mask;
            for (op, expected) in [
                (Binary::Add, left.wrapping_add(literal)),
                (Binary::Subtract, left.wrapping_sub(literal)),
                (Binary::And, left & literal),
                (Binary::Or, left | literal),
                (Binary::Xor, left ^ literal),
            ] {
                let mut f = function(
                    ty,
                    Op::Binary {
                        op,
                        left: 0,
                        right: 3,
                    },
                );
                f.locals = vec![Type::Native; locals];
                f.blocks[0].instructions.insert(
                    0,
                    Instruction {
                        result: Some((3, ty)),
                        op: Op::Constant(literal),
                        il_offset: 0,
                    },
                );
                cases.push((f, vec![left, 0], Ok(expected & mask)));
            }
        }
    }
    run(host, &cases);
}

#[rstest::rstest]
#[case(Type::I32, false)]
#[case(Type::I32, true)]
#[case(Type::I64, false)]
#[case(Type::I64, true)]
fn terminal_comparisons_preserve_predicates_and_escaping_booleans(
    host: &Host,
    #[case] ty: Type,
    #[case] escape: bool,
) {
    let mask = if ty == Type::I64 {
        u64::MAX
    } else {
        u32::MAX as u64
    };
    let sign = 1u64 << (ty.bits() - 1);
    let mut cases = Vec::new();
    for op in [
        Comparison::Equal,
        Comparison::LessUnsigned,
        Comparison::GreaterUnsigned,
        Comparison::LessSigned,
        Comparison::GreaterSigned,
    ] {
        let mut f = function(
            ty,
            Op::Compare {
                op,
                left: 0,
                right: 1,
            },
        );
        f.returns = Some(Type::I32);
        f.blocks[0].instructions[0].result = Some((2, Type::I32));
        for (id, value) in [(3, 11), (4, 22)] {
            f.blocks[0].instructions.insert(
                0,
                Instruction {
                    result: Some((id, Type::I32)),
                    op: Op::Constant(value),
                    il_offset: 0,
                },
            );
        }
        f.blocks[0].terminator.kind = Control::Branch {
            condition: 2,
            yes: Edge {
                block: 1,
                arguments: if escape { vec![2] } else { vec![] },
            },
            no: Edge {
                block: 2,
                arguments: if escape { vec![2] } else { vec![] },
            },
        };
        for (parameter, value) in [(5, 3), (6, 4)] {
            f.blocks.push(Block {
                parameters: if escape {
                    vec![(parameter, Type::I32)]
                } else {
                    vec![]
                },
                instructions: vec![],
                terminator: Terminator {
                    kind: Control::Return(Some(if escape { parameter } else { value })),
                    il_offset: 1,
                },
            });
        }
        for (left, right) in [
            (0, 0),
            (0, 1),
            (1, 0),
            (mask, 0),
            (0, mask),
            (sign, mask),
            (mask, sign),
        ] {
            let signed = |value| {
                if ty == Type::I32 {
                    value as u32 as i32 as i64
                } else {
                    value as i64
                }
            };
            let truth = match op {
                Comparison::Equal => left == right,
                Comparison::LessUnsigned => left < right,
                Comparison::GreaterUnsigned => left > right,
                Comparison::LessSigned => signed(left) < signed(right),
                Comparison::GreaterSigned => signed(left) > signed(right),
            };
            let expected = if escape {
                u64::from(truth)
            } else if truth {
                11
            } else {
                22
            };
            let mut optimized = f.clone();
            koh_compile::optimize::simplify(&mut optimized).unwrap();
            for function in [f.clone(), optimized] {
                cases.push((function, vec![left, right], Ok(expected)));
            }
        }
    }
    run(host, &cases);
}

#[rstest::rstest]
#[case("Debug")]
#[case("Release")]
fn roslyn_methods_execute_as_sm83(host: &Host, #[case] configuration: &str) {
    let game = host.directory.path().join(format!("game-{configuration}"));
    std::fs::create_dir_all(&game).unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Integers");
    for name in ["Integers.csproj", "Arithmetic.cs"] {
        std::fs::copy(fixture.join(name), game.join(name)).unwrap();
    }
    let output = dotnet()
        .arg("build")
        .arg(game.join("Integers.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let bytes =
        std::fs::read(game.join(format!("bin/{configuration}/net10.0/Integers.dll"))).unwrap();
    let resolver = koh_compile::resolve::Resolver::new(&[bytes.as_slice()]).unwrap();
    let assembly = resolver.assembly(0).unwrap();
    let mut cases = Vec::new();
    for (name, arguments, expected) in [
        ("Sum", vec![], 55),
        ("Choose", vec![u32::MAX as u64], 7),
        ("Choose", vec![0], 9),
        ("Wrap", vec![0x7fff_ffff], 0x8000_0000),
        ("Wide", vec![3], 0x60_0000_0000),
        ("Wide", vec![u64::MAX], 0x1f_ffff_ffff),
        ("Truncate", vec![255], 0xffff_ffff),
        ("Extend", vec![u32::MAX as u64], u64::MAX),
        ("Small", vec![255], 0xffff_ffff),
    ] {
        let token = assembly
            .metadata()
            .method_defs
            .iter()
            .enumerate()
            .find(|(_, row)| assembly.metadata().strings.get(row.name).unwrap() == name)
            .map(|(i, _)| 0x06000001 + i as u32)
            .unwrap();
        let f = koh_compile::lower::assembly_method(&resolver, Definition { assembly: 0, token })
            .unwrap();
        cases.push((f, arguments, Ok(expected)));
    }
    run(host, &cases);
}

#[rstest::rstest]
fn widths_comparisons_shifts_and_conversions_execute(host: &Host) {
    let mut cases = Vec::new();
    for (ty, mask, sign) in [
        (Type::I32, 0xffff_ffff, 0x8000_0000),
        (Type::I64, u64::MAX, 0x8000_0000_0000_0000),
        (Type::Native, 0xffff, 0x8000),
    ] {
        for (op, left, right, expected) in [
            (Comparison::Equal, mask, mask, 1),
            (Comparison::Equal, sign, 0, 0),
            (Comparison::LessSigned, mask, 1, 1),
            (Comparison::GreaterSigned, 1, mask, 1),
            (Comparison::LessUnsigned, mask, 1, 0),
            (Comparison::GreaterUnsigned, mask, 1, 1),
            (Comparison::LessSigned, sign, mask, 1),
            (Comparison::GreaterSigned, mask, sign, 1),
        ] {
            let mut f = function(
                ty,
                Op::Compare {
                    op,
                    left: 0,
                    right: 1,
                },
            );
            f.returns = Some(Type::I32);
            f.blocks[0].instructions[0].result = Some((2, Type::I32));
            cases.push((f, vec![left, right], Ok(expected)));
        }
        for (op, left, right, expected) in [
            (Binary::ShiftLeft, 1, u64::from(ty.bits()), 1),
            (Binary::ShiftLeft, 1, u64::from(ty.bits() + 1), 2),
            (Binary::ShiftRightSigned, mask, 1, mask),
            (Binary::ShiftRightUnsigned, mask, 1, mask >> 1),
            (Binary::ShiftRightSigned, sign, 1, (sign >> 1) | sign),
        ] {
            let mut f = function(
                ty,
                Op::Binary {
                    op,
                    left: 0,
                    right: 1,
                },
            );
            f.parameters[1] = Type::I32;
            f.blocks[0].parameters[1].1 = Type::I32;
            cases.push((f, vec![left, right], Ok(expected)));
        }
        for (op, input, expected) in [
            (Op::Negate(0), 1, mask),
            (Op::Negate(0), sign, sign),
            (Op::Not(0), 0, mask),
        ] {
            let mut f = function(ty, op);
            f.parameters.truncate(1);
            f.blocks[0].parameters.truncate(1);
            cases.push((f, vec![input], Ok(expected)));
        }
    }
    for (source, target, bits, signed, input, expected) in [
        (Type::I32, Type::I64, 64, true, 0xffff_ffff, u64::MAX),
        (Type::I32, Type::I64, 64, false, 0xffff_ffff, 0xffff_ffff),
        (Type::I64, Type::I32, 8, true, 0x1234_ff, 0xffff_ffff),
        (Type::I64, Type::I32, 8, false, u64::MAX, 0xff),
        (Type::I64, Type::Native, 16, false, u64::MAX, 0xffff),
        (Type::Native, Type::I64, 64, true, 0xffff, u64::MAX),
    ] {
        let mut f = function(
            source,
            Op::Convert {
                value: 0,
                bits,
                signed,
            },
        );
        f.parameters.truncate(1);
        f.blocks[0].parameters.truncate(1);
        f.returns = Some(target);
        f.blocks[0].instructions[0].result = Some((2, target));
        cases.push((f, vec![input], Ok(expected)));
    }
    run(host, &cases);
}

#[rstest::rstest]
#[case(0, false)]
#[case(20, false)]
#[case(0, true)]
#[case(20, true)]
fn memory_copy_truncates_count_and_preserves_stack(
    host: &Host,
    #[case] locals: usize,
    #[case] constant: bool,
) {
    let mut f = function(Type::I32, Op::Constant(0));
    f.parameters = vec![Type::Native, Type::Native, Type::I32];
    f.returns = None;
    f.locals = vec![Type::I64; locals];
    f.blocks[0].parameters = vec![(0, Type::Native), (1, Type::Native), (2, Type::I32)];
    f.blocks[0].terminator.kind = Control::Return(None);
    let pattern = 0x8877665544332211u64;
    let mut cases = Vec::new();
    for count in [0u64, 1, 2, 7, 8, 0x10000, 0x10002] {
        f.blocks[0].instructions = vec![
            Instruction {
                result: Some((3, Type::I64)),
                op: Op::Constant(pattern),
                il_offset: 0,
            },
            Instruction {
                result: None,
                op: Op::StoreMemory {
                    address: 1,
                    value: 3,
                    bits: 64,
                },
                il_offset: 0,
            },
        ];
        if constant {
            f.blocks[0].instructions.push(Instruction {
                result: Some((4, Type::I32)),
                op: Op::Constant(count),
                il_offset: 0,
            });
        }
        f.blocks[0].instructions.push(Instruction {
            result: None,
            op: Op::MemoryCopy {
                destination: 0,
                source: 1,
                count: if constant { 4 } else { 2 },
            },
            il_offset: 1,
        });
        let bytes = count & 0xffff;
        let expected = if bytes == 8 {
            pattern
        } else {
            pattern & ((1u64 << (bytes * 8)) - 1)
        };
        cases.push((f.clone(), vec![0xc008, 0xc018, count], Ok(expected)));
    }
    run(host, &cases);
    run_at(host, &[(f, vec![0xc008, 0xc018, 2], Err(1))], 0xc120);
}

#[rstest::rstest]
#[case(0)]
#[case(20)]
fn vblank_copy_zero_count_and_lcd_off_are_explicit(host: &Host, #[case] locals: usize) {
    let mut f = function(Type::I32, Op::Constant(0));
    f.parameters = vec![Type::Native, Type::Native, Type::I32];
    f.locals = vec![Type::I64; locals];
    f.blocks[0].parameters = vec![(0, Type::Native), (1, Type::Native), (2, Type::I32)];
    f.blocks[0].instructions = vec![
        Instruction {
            result: Some((4, Type::Native)),
            op: Op::Constant(0xff40),
            il_offset: 0,
        },
        Instruction {
            result: Some((5, Type::I32)),
            op: Op::Constant(0),
            il_offset: 0,
        },
        Instruction {
            result: None,
            op: Op::StoreMemory {
                address: 4,
                value: 5,
                bits: 8,
            },
            il_offset: 0,
        },
        Instruction {
            result: Some((3, Type::I32)),
            op: Op::VBlankCopy {
                destination: 0,
                source: 1,
                count: 2,
            },
            il_offset: 1,
        },
    ];
    let off = f.blocks[0].instructions.split_off(2);
    let instruction = |id, ty, op| Instruction {
        result: Some((id, ty)),
        op,
        il_offset: 0,
    };
    f.blocks[0].instructions.extend([
        instruction(6, Type::Native, Op::Constant(0xff44)),
        instruction(7, Type::I32, Op::Constant(144)),
        instruction(8, Type::I32, Op::Constant(128)),
        instruction(
            9,
            Type::I32,
            Op::LoadMemory {
                address: 4,
                bits: 8,
                signed: false,
            },
        ),
        instruction(
            10,
            Type::I32,
            Op::Binary {
                op: Binary::And,
                left: 9,
                right: 8,
            },
        ),
    ]);
    f.blocks[0].terminator.kind = Control::Branch {
        condition: 10,
        yes: Edge {
            block: 3,
            arguments: vec![],
        },
        no: Edge {
            block: 2,
            arguments: vec![],
        },
    };
    f.blocks.push(Block {
        parameters: vec![],
        instructions: vec![
            instruction(
                11,
                Type::I32,
                Op::LoadMemory {
                    address: 6,
                    bits: 8,
                    signed: false,
                },
            ),
            instruction(
                12,
                Type::I32,
                Op::Compare {
                    op: Comparison::LessUnsigned,
                    left: 11,
                    right: 7,
                },
            ),
        ],
        terminator: Terminator {
            il_offset: 0,
            kind: Control::Branch {
                condition: 12,
                yes: Edge {
                    block: 1,
                    arguments: vec![],
                },
                no: Edge {
                    block: 2,
                    arguments: vec![],
                },
            },
        },
    });
    f.blocks.push(Block {
        parameters: vec![],
        instructions: off,
        terminator: Terminator {
            il_offset: 2,
            kind: Control::Return(Some(3)),
        },
    });
    f.blocks.push(Block {
        parameters: vec![],
        instructions: vec![
            instruction(
                13,
                Type::I32,
                Op::LoadMemory {
                    address: 6,
                    bits: 8,
                    signed: false,
                },
            ),
            instruction(
                14,
                Type::I32,
                Op::Compare {
                    op: Comparison::LessUnsigned,
                    left: 13,
                    right: 7,
                },
            ),
        ],
        terminator: Terminator {
            il_offset: 0,
            kind: Control::Branch {
                condition: 14,
                yes: Edge {
                    block: 1,
                    arguments: vec![],
                },
                no: Edge {
                    block: 3,
                    arguments: vec![],
                },
            },
        },
    });
    let cases = [0u64, 0x10000, 1, 0x10001].map(|count| {
        (
            f.clone(),
            vec![0xffff, 0xffff, count],
            if count as u16 == 0 { Ok(0) } else { Err(9) },
        )
    });
    run(host, &cases);
}

#[rstest::rstest]
#[case(0, false)]
#[case(20, false)]
#[case(0, true)]
#[case(20, true)]
fn memory_fill_truncates_formal_widths_and_preserves_stack(
    host: &Host,
    #[case] locals: usize,
    #[case] constant: bool,
) {
    let mut f = function(Type::I32, Op::Constant(0));
    f.parameters = vec![Type::Native, Type::I32, Type::I32];
    f.returns = None;
    f.locals = vec![Type::I64; locals];
    f.blocks[0].parameters = vec![(0, Type::Native), (1, Type::I32), (2, Type::I32)];
    f.blocks[0].terminator.kind = Control::Return(None);
    let mut cases = Vec::new();
    for count in [0u64, 1, 2, 7, 8, 0x10000, 0x10002] {
        f.blocks[0].instructions.clear();
        if constant {
            f.blocks[0].instructions.push(Instruction {
                result: Some((3, Type::I32)),
                op: Op::Constant(count),
                il_offset: 0,
            });
        }
        f.blocks[0].instructions.push(Instruction {
            result: None,
            op: Op::MemoryFill {
                address: 0,
                value: 1,
                count: if constant { 3 } else { 2 },
            },
            il_offset: 1,
        });
        let expected = (0..(count & 0xffff)).fold(0, |bits, byte| bits | (0xa7u64 << (byte * 8)));
        cases.push((f.clone(), vec![0xc008, 0x123456a7, count], Ok(expected)));
    }
    run(host, &cases);
    run_at(host, &[(f, vec![0xc008, 0xa7, 2], Err(1))], 0xc120);
}

#[rstest::rstest]
#[case(Type::I32, Binary::ShiftLeft, 1, 31, 0x80000000)]
#[case(Type::I64, Binary::ShiftLeft, 1, 63, 0x8000000000000000)]
#[case(Type::I32, Binary::ShiftRightSigned, 0x80000000, 31, 0xffffffff)]
#[case(Type::I64, Binary::ShiftRightSigned, 0x8000000000000000, 63, u64::MAX)]
#[case(Type::I32, Binary::ShiftRightUnsigned, 0x80000000, 31, 1)]
#[case(Type::I64, Binary::ShiftRightUnsigned, 0x8000000000000000, 63, 1)]
fn wide_shift_cycle_cost(
    host: &Host,
    #[case] ty: Type,
    #[case] op: Binary,
    #[case] value: u64,
    #[case] count: u64,
    #[case] expected: u64,
) {
    let mut f = function(
        ty,
        Op::Binary {
            op,
            left: 0,
            right: 1,
        },
    );
    f.parameters[1] = Type::I32;
    f.blocks[0].parameters[1].1 = Type::I32;
    let directory = tempfile::tempdir_in(host.directory.path()).unwrap();
    let path = directory.path().join("shift.gb");
    std::fs::write(&path, rom(&f, &[value, count], 0xd000)).unwrap();
    let output = dotnet()
        .arg(&host.dll)
        .arg("--cost")
        .arg(path)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let stdout = String::from_utf8(output.stdout).unwrap();
    let fields: Vec<_> = stdout.split_whitespace().collect();
    assert_eq!(fields[0], "A5", "{stdout}");
    let bytes: String = expected
        .to_le_bytes()
        .iter()
        .map(|b| format!("{b:02X}"))
        .collect();
    assert_eq!(fields[2], bytes, "{stdout}");
    assert_eq!(&fields[3..5], &["D000", "OK"], "{stdout}");
    let cycles: u64 = fields[5].parse().unwrap();
    println!("{ty:?} {op:?}: {cycles} T-cycles");
    assert!(cycles < 24000, "wide shift exceeds24000 T-cycles: {stdout}");
}

#[rstest::rstest]
#[case(0)]
#[case(20)]
#[case(160)]
fn wide_division_stack_boundary_preserves_reserved_memory(host: &Host, #[case] locals: usize) {
    let mut f = function(
        Type::I64,
        Op::Binary {
            op: Binary::DivideUnsigned,
            left: 0,
            right: 1,
        },
    );
    f.locals = vec![Type::I64; locals];
    let directory = tempfile::tempdir_in(host.directory.path()).unwrap();
    let mut command = dotnet();
    command.arg(&host.dll).current_dir(directory.path());
    let stacks: Vec<u16> = (0xc140..0xc300).step_by(2).collect();
    for &stack in &stacks {
        let path = directory.path().join(format!("{stack:04x}.gb"));
        std::fs::write(&path, rom(&f, &[u64::MAX, 0x100000011], stack)).unwrap();
        command.arg(format!("{stack:04x}.gb"));
    }
    let result = command.output().unwrap();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    let output = String::from_utf8(result.stdout).unwrap();
    let lines: Vec<_> = output.lines().collect();
    assert_eq!(lines.len(), stacks.len());
    let expected: String = (u64::MAX / 0x100000011u64)
        .to_le_bytes()
        .iter()
        .map(|b| format!("{b:02X}"))
        .collect();
    let (mut returned, mut trapped) = (0, 0);
    for (line, stack) in lines.into_iter().zip(stacks) {
        let fields: Vec<_> = line.split_whitespace().collect();
        assert_eq!(fields[4], "OK", "SP={stack:04x}: {line}");
        match fields[0] {
            "A5" => {
                assert_eq!(fields[2], expected, "{line}");
                assert_eq!(fields[3], format!("{stack:04X}"), "{line}");
                returned += 1;
            }
            "EE" => {
                assert_eq!(fields[1], "01", "{line}");
                trapped += 1;
            }
            _ => panic!("Unexpected termination: {line}"),
        }
    }
    assert!(
        returned > 0 && trapped > 0,
        "sweep must cross the stack boundary"
    );
}

#[rstest::rstest]
#[case(Type::I32)]
#[case(Type::I64)]
fn repeated_products_fit_a_rom_bank_and_preserve_frames(host: &Host, #[case] ty: Type) {
    let mut f = function(
        ty,
        Op::Binary {
            op: Binary::Multiply,
            left: 0,
            right: 1,
        },
    );
    f.locals = vec![Type::I64; 20];
    f.blocks[0].instructions = (2..42)
        .map(|id| Instruction {
            result: Some((id, ty)),
            op: Op::Binary {
                op: Binary::Multiply,
                left: if id == 2 { 0 } else { id - 1 },
                right: 1,
            },
            il_offset: id,
        })
        .collect();
    f.blocks[0].terminator.kind = Control::Return(Some(41));
    run(host, &[(f, vec![30, 1], Ok(30))]);
}

#[rstest::rstest]
#[case(Type::I32, 0xffff_ffe2, 0xffff_ff81)]
#[case(Type::I64, (-30i64) as u64, (-127i64) as u64)]
fn small_multiplication_cycle_cost(
    host: &Host,
    #[case] ty: Type,
    #[case] left: u64,
    #[case] right: u64,
) {
    let f = function(
        ty,
        Op::Binary {
            op: Binary::Multiply,
            left: 0,
            right: 1,
        },
    );
    let directory = tempfile::tempdir_in(host.directory.path()).unwrap();
    let path = directory.path().join("multiply.gb");
    std::fs::write(&path, rom(&f, &[left, right], 0xd000)).unwrap();
    let output = dotnet()
        .arg(&host.dll)
        .arg("--cost")
        .arg(path)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let stdout = String::from_utf8(output.stdout).unwrap();
    let fields: Vec<_> = stdout.split_whitespace().collect();
    assert_eq!(fields[0], "A5", "{stdout}");
    assert_eq!(fields[2], "E20E000000000000", "{stdout}");
    assert_eq!(fields[3], "D000", "{stdout}");
    assert_eq!(fields[4], "OK", "{stdout}");
    let cycles: u64 = fields[5].parse().unwrap();
    println!("{ty:?}: {cycles} T-cycles");
    assert!(
        cycles < 20000,
        "small {ty:?} multiplication costs {cycles} T-cycles; budget 20000"
    );
}

#[rstest::rstest]
#[case(Type::Native, 0)]
#[case(Type::Native, 20)]
#[case(Type::I32, 0)]
#[case(Type::I32, 20)]
#[case(Type::I64, 0)]
#[case(Type::I64, 20)]
fn multiplication_dispatch_preserves_modular_width(
    host: &Host,
    #[case] ty: Type,
    #[case] locals: usize,
) {
    let mask = if ty == Type::I64 {
        u64::MAX
    } else {
        (1u64 << ty.bits()) - 1
    };
    let sign = 1u64 << (ty.bits() - 1);
    let mut cases = Vec::new();
    for (left, right) in [
        (0, 0),
        (255, 255),
        (256, 255),
        (255, 256),
        (mask, mask),
        (mask - 254, 255),
        (255, mask - 254),
        (mask - 255, 255),
        (sign, mask),
        (sign, 2),
        (sign, sign),
        (sign + 1, 3),
    ] {
        let mut f = function(
            ty,
            Op::Binary {
                op: Binary::Multiply,
                left: 0,
                right: 1,
            },
        );
        f.locals = vec![Type::I64; locals];
        cases.push((f, vec![left, right], Ok(left.wrapping_mul(right) & mask)));
    }
    run(host, &cases);
}

#[rstest::rstest]
#[case(Type::I32)]
#[case(Type::I64)]
fn narrow_spills_preserve_full_width_arithmetic_and_call_results(host: &Host, #[case] ty: Type) {
    let mask = if ty == Type::I64 {
        u64::MAX
    } else {
        u64::from(u32::MAX)
    };
    let mut cases = Vec::new();
    let normalized = |op| {
        let mut f = function(ty, op);
        f.blocks[0].instructions.insert(
            0,
            Instruction {
                result: Some((3, ty)),
                op: Op::Convert {
                    value: 0,
                    bits: 8,
                    signed: false,
                },
                il_offset: 1,
            },
        );
        f.blocks[0].instructions.insert(
            1,
            Instruction {
                result: Some((4, ty)),
                op: Op::Convert {
                    value: 1,
                    bits: 8,
                    signed: false,
                },
                il_offset: 2,
            },
        );
        f
    };
    for (op, left, right, expected) in [
        (Binary::Add, 0x1234ff, 0xab01, 256),
        (Binary::Subtract, 0, 1, mask),
        (Binary::Multiply, 255, 255, 65025),
        (Binary::And, 0xff, 0x80, 0x80),
        (Binary::Or, 0x80, 1, 0x81),
        (Binary::Xor, 0xff, 0xf0, 15),
        (Binary::DivideSigned, 255, 2, 127),
        (Binary::RemainderSigned, 255, 2, 1),
        (Binary::DivideUnsigned, 255, 2, 127),
        (Binary::RemainderUnsigned, 255, 2, 1),
        (Binary::ShiftRightSigned, 255, 1, 127),
        (Binary::ShiftRightUnsigned, 255, 8, 0),
        (Binary::ShiftRightSigned, 255, 31, 0),
        (
            Binary::ShiftLeft,
            1,
            40,
            if ty == Type::I64 { 1 << 40 } else { 256 },
        ),
    ] {
        let mut f = normalized(Op::Binary {
            op,
            left: 3,
            right: 4,
        });
        if matches!(
            op,
            Binary::ShiftLeft | Binary::ShiftRightSigned | Binary::ShiftRightUnsigned
        ) {
            f.blocks[0].instructions[1].result = Some((4, Type::I32));
        }
        cases.push((f, vec![left, right], Ok(expected)));
    }
    for (op, expected) in [(Op::Negate(3), mask - 254), (Op::Not(3), mask ^ 255)] {
        cases.push((normalized(op), vec![255, 0], Ok(expected)));
    }
    for (comparison, left, right, signed, expected) in [
        (Comparison::LessSigned, 255, 1, false, 0),
        (Comparison::GreaterSigned, 128, 1, false, 1),
        (Comparison::LessSigned, mask, 1, true, 1),
        (Comparison::Equal, 255, 255, false, 1),
    ] {
        let mut f = normalized(Op::Compare {
            op: comparison,
            left: 3,
            right: 4,
        });
        f.blocks[0].instructions[0].op = Op::Convert {
            value: 0,
            bits: 8,
            signed,
        };
        f.blocks[0].instructions[2].result = Some((2, Type::I32));
        f.returns = Some(Type::I32);
        cases.push((f, vec![left, right], Ok(expected)));
    }
    let mut f = normalized(Op::Binary {
        op: Binary::Multiply,
        left: 3,
        right: 5,
    });
    f.blocks[0].instructions.insert(
        2,
        Instruction {
            result: Some((5, ty)),
            op: Op::Constant(4),
            il_offset: 3,
        },
    );
    cases.push((f, vec![255, 0], Ok(1020)));
    for signed in [false, true] {
        let mut f = normalized(Op::Convert {
            value: 3,
            bits: 16,
            signed,
        });
        f.blocks[0].instructions[2].result = Some((2, Type::Native));
        f.returns = Some(Type::Native);
        cases.push((f, vec![255, 0], Ok(255)));
    }
    let mut f = normalized(Op::Convert {
        value: 3,
        bits: 8,
        signed: true,
    });
    cases.push((f.clone(), vec![255, 0], Ok(mask)));
    f.blocks[0].instructions[0].op = Op::Convert {
        value: 0,
        bits: 16,
        signed: false,
    };
    f.blocks[0].instructions[2].op = Op::Binary {
        op: Binary::ShiftRightSigned,
        left: 3,
        right: 4,
    };
    f.blocks[0].instructions[1].result = Some((4, Type::I32));
    cases.push((f, vec![65535, 1], Ok(32767)));
    run(host, &cases);
}

#[rstest::rstest]
#[case(Scalar::U8, 256, 255, 0)]
#[case(Scalar::U16, 65536, 65535, 8)]
fn partial_local_aliases_preserve_high_bits_and_carry(
    host: &Host,
    #[case] scalar: Scalar,
    #[case] initial: u64,
    #[case] rewritten: u64,
    #[case] shift: u64,
) {
    let mut f = function(Type::I32, Op::Constant(0));
    f.parameters.clear();
    f.locals = vec![Type::I32];
    f.blocks[0].parameters.clear();
    let instruction = |id, ty, op| Instruction {
        result: Some((id, ty)),
        op,
        il_offset: id,
    };
    f.blocks[0].instructions = vec![
        instruction(0, Type::I32, Op::Constant(initial)),
        Instruction {
            result: None,
            op: Op::StoreLocal { local: 0, value: 0 },
            il_offset: 0,
        },
        instruction(
            1,
            Type::ByRef(scalar),
            Op::LocalAddress {
                local: 0,
                storage: FieldStorage::Value(Storage::Scalar(scalar)),
            },
        ),
        instruction(2, Type::I32, Op::Constant(rewritten)),
        Instruction {
            result: None,
            op: Op::StoreMemory {
                address: 1,
                value: 2,
                bits: u16::from(scalar.bits()),
            },
            il_offset: 1,
        },
        instruction(3, Type::I32, Op::LoadLocal(0)),
        instruction(4, Type::I32, Op::Constant(shift)),
        instruction(
            5,
            Type::I32,
            Op::Binary {
                op: Binary::ShiftRightUnsigned,
                left: 3,
                right: 4,
            },
        ),
        instruction(6, Type::I32, Op::Constant(0xfeff)),
        instruction(
            7,
            Type::I32,
            Op::Binary {
                op: Binary::Add,
                left: 5,
                right: 6,
            },
        ),
    ];
    f.blocks[0].terminator.kind = Control::Return(Some(7));
    run(host, &[(f, vec![], Ok(0x100fe))]);
}

#[rstest::rstest]
#[case(Type::I32)]
#[case(Type::I64)]
fn small_division_cycle_cost(host: &Host, #[case] ty: Type) {
    let f = function(
        ty,
        Op::Binary {
            op: Binary::DivideSigned,
            left: 0,
            right: 1,
        },
    );
    let directory = tempfile::tempdir_in(host.directory.path()).unwrap();
    let path = directory.path().join("division.gb");
    std::fs::write(&path, rom(&f, &[30000, 31], 0xd000)).unwrap();
    let output = dotnet()
        .arg(&host.dll)
        .arg("--cost")
        .arg(path)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let stdout = String::from_utf8(output.stdout).unwrap();
    let fields: Vec<_> = stdout.split_whitespace().collect();
    assert_eq!(fields[0], "A5", "{stdout}");
    assert_eq!(fields[2], "C703000000000000", "{stdout}");
    assert_eq!(fields[3], "D000", "{stdout}");
    assert_eq!(fields[4], "OK", "{stdout}");
    let cycles: u64 = fields[5].parse().unwrap();
    println!("{ty:?}: {cycles} T-cycles");
    assert!(
        cycles < 20000,
        "small {ty:?} division costs {cycles} T-cycles; budget 20000"
    );
}

#[rstest::rstest]
#[case(Type::Native, 0)]
#[case(Type::Native, 20)]
#[case(Type::I32, 0)]
#[case(Type::I32, 20)]
#[case(Type::I64, 0)]
#[case(Type::I64, 20)]
fn division_word_boundaries_execute(host: &Host, #[case] ty: Type, #[case] locals: usize) {
    let mask = if ty == Type::I64 {
        u64::MAX
    } else {
        (1u64 << ty.bits()) - 1
    };
    let mut cases = Vec::new();
    for (left, right) in [
        (0, 1),
        (1, 65535),
        (32768, 1),
        (65535, 32768),
        (65535, 65534),
        (65535, 255),
        (65535, 1),
        (65536, 31),
        (131071, 65536),
    ] {
        if left > mask || right > mask {
            continue;
        }
        for (op, expected) in [
            (Binary::DivideUnsigned, left / right),
            (Binary::RemainderUnsigned, left % right),
        ] {
            let mut f = function(
                ty,
                Op::Binary {
                    op,
                    left: 0,
                    right: 1,
                },
            );
            f.locals = vec![Type::I64; locals];
            cases.push((f, vec![left, right], Ok(expected)));
        }
    }
    for (left, right) in [
        (-32768i64, -1i64),
        (-30000, 31),
        (30000, -31),
        (-30000, -31),
    ] {
        if ty == Type::Native && left == -32768 && right == -1 {
            continue;
        }
        for (op, expected) in [
            (Binary::DivideSigned, left / right),
            (Binary::RemainderSigned, left % right),
        ] {
            let mut f = function(
                ty,
                Op::Binary {
                    op,
                    left: 0,
                    right: 1,
                },
            );
            f.locals = vec![Type::I64; locals];
            cases.push((
                f,
                vec![left as u64 & mask, right as u64 & mask],
                Ok(expected as u64 & mask),
            ));
        }
    }
    run(host, &cases);
}

#[rstest::rstest]
fn division_boundaries_and_faults_execute(host: &Host) {
    let mut cases = Vec::new();
    for (ty, mask, sign) in [
        (Type::I32, 0xffff_ffff, 0x8000_0000),
        (Type::I64, u64::MAX, 0x8000_0000_0000_0000),
        (Type::Native, 0xffff, 0x8000),
    ] {
        for (op, left, right, expected) in [
            (Binary::Multiply, mask, mask, Ok(1)),
            (Binary::DivideUnsigned, mask, sign + 3, Ok(1)),
            (Binary::RemainderUnsigned, mask, sign + 3, Ok(sign - 4)),
            (Binary::DivideSigned, mask - 6, mask - 2, Ok(2)),
            (Binary::RemainderSigned, mask - 6, mask - 2, Ok(mask)),
            (Binary::DivideSigned, 7, mask - 2, Ok(mask - 1)),
            (Binary::RemainderSigned, 7, mask - 2, Ok(1)),
            (Binary::DivideSigned, sign, 1, Ok(sign)),
            (Binary::RemainderSigned, sign, mask, Err(2)),
            (Binary::DivideSigned, sign, mask, Err(2)),
            (Binary::DivideUnsigned, 1, 0, Err(2)),
            (Binary::RemainderUnsigned, 1, 0, Err(2)),
        ] {
            cases.push((
                function(
                    ty,
                    Op::Binary {
                        op,
                        left: 0,
                        right: 1,
                    },
                ),
                vec![left, right],
                expected,
            ));
        }
    }
    run(host, &cases);
}

#[rstest::rstest]
fn cil_switch_executes_full_width_selectors_and_stack_edges(host: &Host) {
    let il = [
        0x1f, 40, 0x02, 0x45, 3, 0, 0, 0, 4, 0, 0, 0, 7, 0, 0, 0, 10, 0, 0, 0, 0x1f, 9, 0x58, 0x2a,
        0x17, 0x58, 0x2a, 0x18, 0x58, 0x2a, 0x19, 0x58, 0x2a,
    ];
    let mut f = koh_compile::lower::lower(koh_compile::lower::Method {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        il: &il,
        parameters: vec![Type::I32],
        returns: Some(Type::I32),
        locals: vec![],
        init_locals: true,
        max_stack: 2,
    })
    .unwrap();
    koh_compile::optimize::simplify(&mut f).unwrap();
    let mut cases: Vec<_> = [0, 1, 2, 3, 256, 65536, 0x80000000, 0xffffffff]
        .into_iter()
        .map(|selector| {
            (
                f.clone(),
                vec![selector],
                Ok(if selector < 3 { 41 + selector } else { 49 }),
            )
        })
        .collect();
    let mut duplicate = il;
    duplicate[16] = 7;
    let empty = [0x1f, 40, 0x02, 0x45, 0, 0, 0, 0, 0x2a];
    let mut loop_il = vec![0x16, 0x0a, 0x06, 0x17, 0x58, 0x0a, 0x06, 0x45, 4, 0, 0, 0];
    for displacement in [-26i32, -26, -26, 0] {
        loop_il.extend_from_slice(&displacement.to_le_bytes());
    }
    loop_il.extend_from_slice(&[0x06, 0x2a]);
    for (il, parameters, locals, arguments, expected) in [
        (duplicate.as_slice(), vec![Type::I32], vec![], vec![2], 42),
        (
            empty.as_slice(),
            vec![Type::I32],
            vec![],
            vec![0xffffffff],
            40,
        ),
        (loop_il.as_slice(), vec![], vec![Type::I32], vec![], 3),
    ] {
        let mut f = koh_compile::lower::lower(koh_compile::lower::Method {
            identity: Definition {
                assembly: 0,
                token: 0x06000001,
            },
            il,
            parameters,
            returns: Some(Type::I32),
            locals,
            init_locals: true,
            max_stack: 2,
        })
        .unwrap();
        koh_compile::optimize::simplify(&mut f).unwrap();
        cases.push((f, arguments, Ok(expected)));
    }
    run(host, &cases);
}

#[rstest::rstest]
fn stack_capacity_is_checked_before_frame_writes(host: &Host) {
    let f = function(
        Type::I32,
        Op::Binary {
            op: Binary::Add,
            left: 0,
            right: 1,
        },
    );
    run_at(host, &[(f.clone(), vec![1, 2], Err(1))], 0xc120);
    let mut multiply = f.clone();
    multiply.blocks[0].instructions[0].op = Op::Binary {
        op: Binary::Multiply,
        left: 0,
        right: 1,
    };
    run_at(host, &[(multiply, vec![30, 127], Err(1))], 0xc120);
    let mut huge = f;
    huge.parameters = vec![Type::I64; 1000];
    huge.blocks[0].parameters = (0..1000).map(|id| (id, Type::I64)).collect();
    huge.blocks[0].instructions.clear();
    huge.returns = Some(Type::I64);
    huge.blocks[0].terminator.kind = Control::Return(Some(0));
    assert!(sm83::emit(&huge).unwrap_err().contains("capacity"));
}

#[rstest::rstest]
fn pinned_native_clear_checks_zero_before_fabricating_a_reference(host: &Host) {
    let f = Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![Type::Native],
        returns: Some(Type::I32),
        locals: vec![Type::ByRef(Scalar::U8)],
        blocks: vec![Block {
            parameters: vec![(0, Type::Native)],
            instructions: vec![
                Instruction {
                    result: Some((1, Type::ByRef(Scalar::U8))),
                    op: Op::Constant(0),
                    il_offset: 0,
                },
                Instruction {
                    result: None,
                    op: Op::StoreLocal { local: 0, value: 1 },
                    il_offset: 0,
                },
                Instruction {
                    result: None,
                    op: Op::StorePinned { local: 0, value: 0 },
                    il_offset: 1,
                },
                Instruction {
                    result: Some((2, Type::I32)),
                    op: Op::Constant(42),
                    il_offset: 2,
                },
            ],
            terminator: Terminator {
                il_offset: 3,
                kind: Control::Return(Some(2)),
            },
        }],
    };
    let cases: Vec<_> = [0, 1, 0xc100, 0xd000, 0xffff]
        .into_iter()
        .map(|value| {
            (
                f.clone(),
                vec![value],
                if value == 0 { Ok(42) } else { Err(8) },
            )
        })
        .collect();
    run(host, &cases);
}

#[rstest::rstest]
#[case(false, false)]
#[case(false, true)]
#[case(true, false)]
#[case(true, true)]
fn short_and_wide_local_argument_address_encodings_execute(
    host: &Host,
    #[case] argument: bool,
    #[case] wide: bool,
) {
    let mut il = if wide {
        vec![0xfe, if argument { 0x0a } else { 0x0d }, 0, 0]
    } else {
        vec![if argument { 0x0f } else { 0x12 }, 0]
    };
    il.extend_from_slice(&[0x1f, 42, 0x54, if argument { 0x02 } else { 0x06 }, 0x2a]);
    let f = koh_compile::lower::lower(koh_compile::lower::Method {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        il: &il,
        parameters: if argument { vec![Type::I32] } else { vec![] },
        returns: Some(Type::I32),
        locals: if argument { vec![] } else { vec![Type::I32] },
        init_locals: true,
        max_stack: 2,
    })
    .unwrap();
    run(
        host,
        &[(f, if argument { vec![7] } else { vec![] }, Ok(42))],
    );
}

#[rstest::rstest]
#[case(false)]
#[case(true)]
fn null_managed_references_trap_but_native_bus_zero_is_valid(host: &Host, #[case] store: bool) {
    let mut cases = Vec::new();
    for ty in [Type::Native, Type::ByRef(Scalar::I32)] {
        let mut f = function(Type::I32, Op::Constant(0));
        f.parameters[0] = ty;
        f.blocks[0].parameters[0].1 = ty;
        f.blocks[0].instructions = vec![Instruction {
            result: if store { None } else { Some((2, Type::I32)) },
            op: if store {
                Op::StoreMemory {
                    address: 0,
                    value: 1,
                    bits: 32,
                }
            } else {
                Op::LoadMemory {
                    address: 0,
                    bits: 32,
                    signed: false,
                }
            },
            il_offset: 0,
        }];
        f.blocks[0].terminator.kind = Control::Return(Some(if store { 1 } else { 2 }));
        let expected = if matches!(ty, Type::ByRef(_)) {
            Err(4)
        } else {
            Ok(if store { 42 } else { 0 })
        };
        cases.push((f, vec![0, 42], expected));
    }
    run(host, &cases);
}

#[rstest::rstest]
fn indirect_memory_respects_the_last_stack_scratch_word(host: &Host) {
    let f = Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![Type::Native, Type::I32],
        returns: Some(Type::I32),
        locals: vec![],
        blocks: vec![Block {
            parameters: vec![(0, Type::Native), (1, Type::I32)],
            instructions: vec![
                Instruction {
                    result: None,
                    op: Op::StoreMemory {
                        address: 0,
                        value: 1,
                        bits: 32,
                    },
                    il_offset: 0,
                },
                Instruction {
                    result: Some((2, Type::I32)),
                    op: Op::LoadMemory {
                        address: 0,
                        bits: 32,
                        signed: false,
                    },
                    il_offset: 1,
                },
            ],
            terminator: Terminator {
                kind: Control::Return(Some(2)),
                il_offset: 2,
            },
        }],
    };
    run_at(host, &[(f.clone(), vec![0xc020, 42], Ok(42))], 0xc137);
    run_at(host, &[(f, vec![0xc020, 42], Err(1))], 0xc136);
}

#[rstest::rstest]
fn cyclic_block_arguments_transfer_simultaneously(host: &Host) {
    let mut f = function(Type::I32, Op::Constant(0));
    f.blocks[0].terminator.kind = Control::Jump(Edge {
        block: 1,
        arguments: vec![0, 1, 2],
    });
    f.blocks.push(Block {
        parameters: vec![(3, Type::I32), (4, Type::I32), (5, Type::I32)],
        instructions: vec![
            Instruction {
                result: Some((6, Type::I32)),
                op: Op::Constant(1),
                il_offset: 1,
            },
            Instruction {
                result: Some((7, Type::I32)),
                op: Op::Binary {
                    op: Binary::Add,
                    left: 5,
                    right: 6,
                },
                il_offset: 2,
            },
            Instruction {
                result: Some((8, Type::I32)),
                op: Op::Constant(3),
                il_offset: 3,
            },
            Instruction {
                result: Some((9, Type::I32)),
                op: Op::Compare {
                    op: Comparison::LessSigned,
                    left: 5,
                    right: 8,
                },
                il_offset: 4,
            },
        ],
        terminator: Terminator {
            kind: Control::Branch {
                condition: 9,
                yes: Edge {
                    block: 1,
                    arguments: vec![4, 3, 7],
                },
                no: Edge {
                    block: 2,
                    arguments: vec![3],
                },
            },
            il_offset: 5,
        },
    });
    f.blocks.push(Block {
        parameters: vec![(10, Type::I32)],
        instructions: vec![],
        terminator: Terminator {
            kind: Control::Return(Some(10)),
            il_offset: 6,
        },
    });
    run(host, &[(f, vec![11, 22], Ok(22))]);
}

#[test]
fn elf_retains_method_and_il_offset_symbols() {
    let mut f = function(
        Type::I32,
        Op::Binary {
            op: Binary::Add,
            left: 0,
            right: 1,
        },
    );
    f.blocks[0].instructions[0].il_offset = 0x37;
    f.blocks[0].terminator.il_offset = 0x52;
    let object = read_elf(&write_elf(&sm83::emit(&f).unwrap()).unwrap()).unwrap();
    assert!(
        object
            .symbols
            .iter()
            .any(|s| s.name == sm83::symbol(f.identity))
    );
    for marker in [".IL_0037.", ".IL_0052."] {
        assert!(
            object.symbols.iter().any(|s| s.name.contains(marker)),
            "Missing {marker}"
        );
    }
}

#[test]
fn maximum_ir_offset_does_not_panic_during_emission() {
    let mut f = function(
        Type::I32,
        Op::Compare {
            op: Comparison::Equal,
            left: 0,
            right: 1,
        },
    );
    f.blocks[0].instructions[0].il_offset = u32::MAX;
    let result = std::panic::catch_unwind(|| sm83::emit(&f));
    assert!(result.is_ok());
    result.unwrap().unwrap();
}

#[test]
fn oversized_machine_code_is_diagnostic() {
    let mut f = function(Type::I64, Op::Constant(0));
    f.locals = vec![Type::I64];
    f.blocks[0].instructions = (0..20_000)
        .map(|offset| Instruction {
            result: None,
            op: Op::StoreLocal { local: 0, value: 0 },
            il_offset: offset,
        })
        .collect();
    f.blocks[0].terminator.kind = Control::Return(Some(0));
    assert!(sm83::emit(&f).unwrap_err().contains("16 KiB"));
}

#[rstest::rstest]
#[case("Debug")]
#[case("Release")]
fn roslyn_reachable_calls_and_recursion_execute(host: &Host, #[case] configuration: &str) {
    let directory = host.directory.path().join(format!("calls-{configuration}"));
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Calls");
    for project in ["Entry", "Bridge", "Runtime"] {
        std::fs::create_dir_all(directory.join(project)).unwrap();
        for file in std::fs::read_dir(fixture.join(project)).unwrap() {
            let file = file.unwrap();
            std::fs::copy(file.path(), directory.join(project).join(file.file_name())).unwrap();
        }
    }
    let output = dotnet()
        .arg("build")
        .arg(directory.join("Entry/Entry.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .arg("-p:DisableTransitiveProjectReferences=true")
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let images: Vec<_> = [
        ("Entry", "Game.Calls"),
        ("Bridge", "Game.Bridge"),
        ("Runtime", "Game.Runtime"),
    ]
    .iter()
    .map(|(project, assembly)| {
        std::fs::read(directory.join(format!(
            "{project}/bin/{configuration}/net10.0/{assembly}.dll"
        )))
        .unwrap()
    })
    .collect();
    let views: Vec<_> = images.iter().map(Vec::as_slice).collect();
    let resolver = koh_compile::resolve::Resolver::new(&views).unwrap();
    let assembly = resolver.assembly(0).unwrap();
    let token = |name| {
        assembly
            .metadata()
            .method_defs
            .iter()
            .enumerate()
            .find(|(_, row)| assembly.metadata().strings.get(row.name).unwrap() == name)
            .map(|(i, _)| 0x06000001 + i as u32)
            .unwrap()
    };
    let mut cases = Vec::new();
    for (name, expected) in [
        ("Main", Ok(42)),
        ("Wide", Ok(0x123456789ABCDEF9)),
        ("Live", Ok(0x112233445566778B)),
        ("Mutual", Ok(1)),
        ("VoidCalls", Ok(9)),
        ("Generic", Ok(1)),
        ("StackValue", Ok(5)),
        ("Deep", Err(1)),
    ] {
        let entry = Definition {
            assembly: 0,
            token: token(name),
        };
        let functions = koh_compile::compile::reachable(&resolver, entry).unwrap();
        if name == "Main" {
            let mut names: Vec<_> = functions
                .iter()
                .map(|f| {
                    resolver
                        .assembly(f.identity.assembly)
                        .unwrap()
                        .method_name(f.identity.token)
                        .unwrap()
                })
                .collect();
            names.sort();
            assert_eq!(
                names,
                [
                    "Game::Play",
                    "Program::Main",
                    "Runtime::Answer",
                    "Runtime::Sum"
                ]
            );
        }
        cases.push((rom_program(&functions, entry, &[], 0xd000), expected));
    }
    run_roms(host, &cases, 0xd000);
    for (name, diagnostic) in [
        ("Dead", "System.Runtime"),
        ("DeadThrow", "opcode"),
        ("Protected", "sections"),
        ("StaticInit", ".cctor"),
    ] {
        let error = koh_compile::compile::reachable(
            &resolver,
            Definition {
                assembly: 0,
                token: token(name),
            },
        )
        .unwrap_err();
        assert!(
            error.contains(diagnostic) && error.contains("IL_"),
            "{name}: {error}"
        );
    }
    let missing = koh_compile::resolve::Resolver::new(&views[..2]).unwrap();
    let error = koh_compile::compile::reachable(
        &missing,
        Definition {
            assembly: 0,
            token: assembly.entry_point,
        },
    )
    .unwrap_err();
    assert!(
        error.contains("Game.Runtime") && error.contains("IL_"),
        "{error}"
    );
    let module_output = directory.join("module-initializer");
    let output = dotnet()
        .arg("build")
        .arg(directory.join("Entry/Entry.csproj"))
        .args([
            "-c",
            configuration,
            "--nologo",
            "-v:q",
            "-m:1",
            "-p:DefineConstants=MODULE_INIT",
            "-p:DisableTransitiveProjectReferences=true",
            "-o",
        ])
        .arg(&module_output)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let module_bytes = std::fs::read(module_output.join("Game.Calls.dll")).unwrap();
    let mut module_views = views;
    module_views[0] = &module_bytes;
    let module = koh_compile::resolve::Resolver::new(&module_views).unwrap();
    let error = koh_compile::compile::reachable(
        &module,
        Definition {
            assembly: 0,
            token: module.assembly(0).unwrap().entry_point,
        },
    )
    .unwrap_err();
    assert!(
        error.contains("InitModule") && error.contains("opcode") && error.contains("IL_"),
        "{error}"
    );
}

#[rstest::rstest]
#[case(0)]
#[case(20)]
fn vram_write_guards_addresses_with_large_frames(host: &Host, #[case] locals: usize) {
    let mut f = function(Type::I32, Op::Constant(42));
    f.parameters = vec![Type::Native, Type::I32];
    f.locals = vec![Type::I64; locals];
    f.blocks[0].parameters = vec![(0, Type::Native), (1, Type::I32)];
    f.blocks[0].instructions = vec![
        Instruction {
            result: None,
            op: Op::VramWrite {
                address: 0,
                value: 1,
            },
            il_offset: 1,
        },
        Instruction {
            result: Some((2, Type::I32)),
            op: Op::Constant(42),
            il_offset: 2,
        },
    ];
    f.blocks[0].terminator.kind = Control::Return(Some(2));
    let cases = [0x7fff, 0x8000, 0x9fff, 0xa000, 0xffff].map(|address| {
        let result = if (0x8000..0xa000).contains(&address) {
            Ok(42)
        } else {
            Err(9)
        };
        (f.clone(), vec![address, 0x16d], result)
    });
    run(host, &cases);
}
