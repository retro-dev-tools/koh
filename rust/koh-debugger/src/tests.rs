use super::*;
use koh_emulator::machine::Machine;
use serde_json::{Value, json};

#[test]
fn debugger_forwards_existing_hook_context_and_hblank_events() {
    use koh_emulator::machine::MemoryContext;
    type Events = Arc<Mutex<Vec<(u8, MemoryContext, u8, i32)>>>;
    struct Trace(Events);
    impl MemoryHook for Trace {
        fn set_context(&mut self, context: MemoryContext) {
            self.0.lock().unwrap().push((b'C', context, 0, 0));
        }
        fn on_hblank(&mut self, context: MemoryContext, ly: u8, dot: i32) {
            self.0.lock().unwrap().push((b'H', context, ly, dot));
        }
        fn on_read(&mut self, _: u16, _: u8, _: u16, _: u8, _: i32, _: PpuMode) {}
        fn on_write(&mut self, _: u16, _: u8, _: u16, _: u8, _: i32, _: PpuMode) {}
    }
    let run = |debugged: bool| {
        let events: Events = Arc::new(Mutex::new(Vec::new()));
        let mut m = machine(&[0x18, 0xfe]);
        m.write_debug(0xff40, 0x91);
        m.hook = Some(Box::new(Trace(events.clone())));
        if debugged {
            assert!(Debugger::default().run_frame(&mut m).is_none());
        } else {
            m.run_frame();
        }
        let result = events.lock().unwrap().clone();
        result
    };
    let reference = run(false);
    assert!(reference.iter().any(|event| event.0 == b'C'));
    assert!(reference.iter().any(|event| event.0 == b'H'));
    let actual = run(true);
    assert_eq!(actual.len(), reference.len());
    for (actual, expected) in actual.iter().zip(&reference) {
        assert_eq!(actual, expected);
    }
}

fn machine(program: &[u8]) -> Machine {
    let mut rom = vec![0; 0x8000];
    rom[0x100..0x100 + program.len()].copy_from_slice(program);
    let mut m = Machine::load_rom(&rom).unwrap();
    m.cpu.registers.pc = 0x100;
    m.cpu.registers.sp = 0xfffe;
    m
}

fn request(d: &mut Debugger, m: &mut Machine, command: &str, args: Value) -> Reply {
    d.handle(
        &json!({"seq":1,"type":"request","command":command,"arguments":args}),
        m,
    )
}

#[test]
fn write_watchpoint_fires_on_matching_cpu_address() {
    let mut m = machine(&[0x3e, 0x22, 0xea, 0x99, 0xc0, 0xea, 0x00, 0xc1, 0x18, 0xfe]);
    let mut d = Debugger::default();
    let r = request(
        &mut d,
        &mut m,
        "setDataBreakpoints",
        json!({"breakpoints":[{"dataId":"$C100","accessType":"write"}]}),
    );
    assert_eq!(r.response["success"], true);
    let stopped = d.run_frame(&mut m).unwrap();
    assert_eq!(stopped["body"]["reason"], "data breakpoint");
    assert_eq!(m.cpu.registers.pc, 0x108);
    assert_eq!(m.read_debug(0xc099), 0x22);
    assert_eq!(m.read_debug(0xc100), 0x22);
}

fn linked(source: &str) -> koh_link::LinkResult {
    let object = koh_asm::assemble(source, "source.asm", false).unwrap();
    koh_link::link(&[object], &Default::default()).unwrap()
}

fn source_machine(source: &str) -> (Debugger, Machine) {
    let result = linked(source);
    let mut m = Machine::load_rom(&result.rom).unwrap();
    m.cpu.registers.pc = 0x100;
    m.cpu.registers.sp = 0xfffe;
    let mut d = Debugger::default();
    d.debug_info.load(&result.dwarf().unwrap()).unwrap();
    (d, m)
}

#[test]
fn read_watchpoint_and_clear_watchpoints() {
    let mut m = machine(&[0xfa, 0x00, 0xc1, 0xfa, 0x00, 0xc2, 0x18, 0xfe]);
    let mut d = Debugger::default();
    request(
        &mut d,
        &mut m,
        "setDataBreakpoints",
        json!({"breakpoints":[{"dataId":"$C200","accessType":"read"}]}),
    );
    let stopped = d.run_frame(&mut m).unwrap();
    assert_eq!(stopped["body"]["reason"], "data breakpoint");
    assert_eq!(m.cpu.registers.pc, 0x106);
    request(
        &mut d,
        &mut m,
        "setDataBreakpoints",
        json!({"breakpoints":[]}),
    );
    m.cpu.registers.pc = 0x100;
    assert!(d.run_frame(&mut m).is_none());
}

#[test]
fn expression_register_memory_literal_and_malformed_conditions() {
    let mut m = machine(&[0]);
    m.cpu.registers.a = 0x42;
    m.cpu.registers.set_hl(0xc100);
    m.write_debug(0xc100, 0x99);
    for text in [
        "A == $42",
        "A > 0x40",
        "[HL] == $99",
        "[$C100] == 153",
        "PC <= 256",
        "BC >= 0",
    ] {
        assert!(expression::condition(&mut m, text), "{text}");
    }
    for text in [
        "A != $42",
        "garbage",
        "A ? 5",
        "[IME] == 0",
        "A == %1000010",
        "[unknown] > 0",
    ] {
        assert!(!expression::condition(&mut m, text), "{text}");
    }
}

#[test]
fn source_conditions_and_hit_counts_gate_real_execution() {
    let (mut d, mut m) = source_machine("SECTION \"code\", ROM0[$100]\nEntry:: inc a\n jr Entry\n");
    m.cpu.registers.a = 0;
    let reply = request(
        &mut d,
        &mut m,
        "setBreakpoints",
        json!({"source":{"path":"SOURCE.ASM"},"breakpoints":[{"line":2,"condition":"A >= 3","hitCondition":">=2"}]}),
    );
    assert_eq!(reply.response["body"]["breakpoints"][0]["verified"], true);
    assert_eq!(d.run_frame(&mut m).unwrap()["body"]["reason"], "breakpoint");
    assert_eq!(m.cpu.registers.a, 3);
    assert_eq!(m.cpu.registers.pc, 0x100);
    let state = d.breakpoints.get(&(0, 0x100)).unwrap();
    assert_eq!(state.hits, 3);
    let unmapped = request(
        &mut d,
        &mut m,
        "setBreakpoints",
        json!({"source":{"path":"source.asm"},"breakpoints":[{"line":999}]}),
    );
    assert_eq!(
        unmapped.response["body"]["breakpoints"][0]["verified"],
        false
    );
    assert!(d.breakpoints.is_empty());
}

#[test]
fn breakpoint_hit_target_three_defers_first_two_hits() {
    let mut d = Debugger::default();
    let mut m = machine(&[0]);
    m.cpu.registers.pc = 0x200;
    d.breakpoints.insert(
        (0, 0x200),
        Breakpoint {
            target: 3,
            ..Default::default()
        },
    );
    assert_eq!(d.breakpoint_reason(&mut m), None);
    assert_eq!(d.breakpoint_reason(&mut m), None);
    assert_eq!(d.breakpoint_reason(&mut m), Some("breakpoint"));
}

#[test]
fn breakpoint_condition_without_hit_target_gates_break() {
    let mut d = Debugger::default();
    let mut m = machine(&[0]);
    m.cpu.registers.pc = 0x200;
    d.breakpoints.insert(
        (0, 0x200),
        Breakpoint {
            condition: Some("A == 1".into()),
            target: 0,
            ..Default::default()
        },
    );
    m.cpu.registers.a = 0;
    assert_eq!(d.breakpoint_reason(&mut m), None);
    m.cpu.registers.a = 1;
    assert_eq!(d.breakpoint_reason(&mut m), Some("breakpoint"));
}

#[test]
fn clearing_write_watchpoints_prevents_subsequent_write_stop() {
    let mut d = Debugger::default();
    let mut m = machine(&[0x3e, 0x42, 0xea, 0x00, 0xc1, 0x18, 0xfe]);
    request(
        &mut d,
        &mut m,
        "setDataBreakpoints",
        json!({"breakpoints":[{"dataId":"$C100","accessType":"write"}]}),
    );
    request(
        &mut d,
        &mut m,
        "setDataBreakpoints",
        json!({"breakpoints":[]}),
    );
    assert!(d.run_frame(&mut m).is_none());
    assert_eq!(m.read_debug(0xc100), 0x42);
}

#[test]
fn initialize_scopes_control_actions_and_sequence_order() {
    let mut d = Debugger::default();
    let mut m = machine(&[0]);
    let reply = request(&mut d, &mut m, "initialize", json!({}));
    assert_eq!(reply.response["body"]["supportsDataBreakpoints"], true);
    assert_eq!(reply.response["body"]["supportsStepBack"], false);
    assert_eq!(reply.response["seq"], 1);
    assert_eq!(reply.events[0]["seq"], 2);
    assert_eq!(reply.events[0]["event"], "initialized");
    let scopes = request(&mut d, &mut m, "scopes", json!({})).response;
    assert_eq!(
        scopes["body"]["scopes"]
            .as_array()
            .unwrap()
            .iter()
            .map(|s| s["name"].as_str().unwrap())
            .collect::<Vec<_>>(),
        ["Registers", "Hardware", "Symbols", "Source Context"]
    );
    assert_eq!(
        request(&mut d, &mut m, "pause", json!({})).action,
        Action::Pause
    );
    assert_eq!(
        request(&mut d, &mut m, "continue", json!({})).action,
        Action::Continue
    );
    assert_eq!(
        request(&mut d, &mut m, "disconnect", json!({})).action,
        Action::Disconnect
    );
    assert_eq!(
        request(&mut d, &mut m, "terminate", json!({})).action,
        Action::Disconnect
    );
    assert_eq!(
        request(&mut d, &mut m, "unknown", json!({})).response["success"],
        false
    );
}

#[test]
fn launch_adopts_current_machine_and_entry_waits_for_configuration_done() {
    let mut d = Debugger::default();
    let mut m = machine(&[0]);
    m.cpu.registers.pc = 0xc000;
    m.cpu.registers.a = 0x42;
    let launch = request(
        &mut d,
        &mut m,
        "launch",
        json!({"program":"does-not-exist.gb","stopOnEntry":true}),
    );
    assert_eq!(launch.action, Action::None);
    assert!(launch.events.is_empty());
    assert_eq!(m.cpu.registers.pc, 0xc000);
    assert_eq!(m.cpu.registers.a, 0x42);
    let configuration = request(&mut d, &mut m, "configurationDone", json!({}));
    assert_eq!(configuration.action, Action::Pause);
    assert_eq!(configuration.events[0]["body"]["reason"], "entry");
    request(&mut d, &mut m, "launch", json!({}));
    assert_eq!(
        request(&mut d, &mut m, "configurationDone", json!({})).action,
        Action::Continue
    );
}

#[test]
fn evaluates_literals_registers_and_unknown_identifier() {
    let mut d = Debugger::default();
    let mut m = machine(&[0x3e, 0x42]);
    d.step(&mut m, Action::StepIn);
    for (expr, result) in [
        ("$100", "$0100 (256)"),
        ("42", "$2A (42)"),
        ("A", "$42 (66)"),
        ("%1010", "$0A (10)"),
        ("-123", "$FFFFFF85 (-123)"),
    ] {
        let response = request(&mut d, &mut m, "evaluate", json!({"expression":expr})).response;
        assert_eq!(response["body"]["result"], result);
    }
    for expr in ["unknown", ""] {
        assert_eq!(
            request(&mut d, &mut m, "evaluate", json!({"expression":expr})).response["success"],
            false
        );
    }
}

#[test]
fn instruction_and_function_breakpoints_replace_flat_set() {
    let (mut d, mut m) = source_machine("SECTION \"code\", ROM0[$100]\nMain:: nop\n jr Main\n");
    let r = request(
        &mut d,
        &mut m,
        "setInstructionBreakpoints",
        json!({"breakpoints":[{"instructionReference":"0x0101"},{"instructionReference":"oops"}]}),
    )
    .response;
    assert_eq!(r["body"]["breakpoints"][0]["verified"], true);
    assert_eq!(r["body"]["breakpoints"][1]["verified"], false);
    assert_eq!(d.run_frame(&mut m).unwrap()["body"]["reason"], "breakpoint");
    assert_eq!(m.cpu.registers.pc, 0x101);
    request(
        &mut d,
        &mut m,
        "setFunctionBreakpoints",
        json!({"breakpoints":[{"name":"main"}]}),
    );
    assert_eq!(d.breakpoints.len(), 1);
    assert!(d.breakpoints.contains_key(&(0, 0x100)));
    assert!(d.run_frame(&mut m).is_some());
    assert_eq!(m.cpu.registers.pc, 0x100);
}

#[test]
fn memory_read_base64_write_rejections_offsets_and_wrapping() {
    let mut d = Debugger::default();
    let mut m = machine(&[0xaa, 0xbb]);
    assert_eq!(
        request(
            &mut d,
            &mut m,
            "readMemory",
            json!({"memoryReference":"0100","count":2})
        )
        .response["body"]["data"],
        "qrs="
    );
    let write = request(
        &mut d,
        &mut m,
        "writeMemory",
        json!({"memoryReference":"$C100","offset":0,"data":"qrvM"}),
    );
    assert_eq!(write.response["body"]["bytesWritten"], 3);
    assert_eq!(
        (
            m.read_debug(0xc100),
            m.read_debug(0xc101),
            m.read_debug(0xc102)
        ),
        (0xaa, 0xbb, 0xcc)
    );
    for args in [
        json!({"memoryReference":"bad-address","data":"AQ=="}),
        json!({"memoryReference":"$C100","data":"!!!"}),
    ] {
        assert_eq!(
            request(&mut d, &mut m, "writeMemory", args).response["success"],
            false
        );
    }
    assert_eq!(
        request(
            &mut d,
            &mut m,
            "readMemory",
            json!({"memoryReference":"0xFFFF","count":5})
        )
        .response["body"]["data"],
        "AA=="
    );
    request(
        &mut d,
        &mut m,
        "writeMemory",
        json!({"memoryReference":"65535","offset":1,"data":"AQ=="}),
    );
    assert_eq!(m.read_debug(0), 1);
}

#[test]
fn step_in_next_call_and_rst_step_out_and_simple_stack() {
    let mut m = machine(&[0xcd, 0, 2, 0]);
    m.cartridge.rom[0x200..0x203].copy_from_slice(&[0, 0, 0xc9]);
    let mut d = Debugger::default();
    assert!(d.step(&mut m, Action::Next).is_empty());
    assert_eq!(d.run_frame(&mut m).unwrap()["body"]["reason"], "step");
    assert_eq!(m.cpu.registers.pc, 0x103);
    assert!(d.one_shots.is_empty());
    assert!(d.breakpoints.is_empty());
    m.cpu.registers.pc = 0x100;
    assert_eq!(d.step(&mut m, Action::StepIn)[0]["body"]["reason"], "step");
    assert_eq!(m.cpu.registers.pc, 0x200);
    let stack = request(&mut d, &mut m, "stackTrace", json!({})).response;
    assert_eq!(stack["body"]["stackFrames"][0]["name"], "$0200");
    assert_eq!(stack["body"]["stackFrames"][1]["name"], "$0103");
    assert!(d.step(&mut m, Action::StepOut).is_empty());
    d.run_frame(&mut m).unwrap();
    assert_eq!(m.cpu.registers.pc, 0x103);
    d.step(&mut m, Action::Next);
    assert_eq!(m.cpu.registers.pc, 0x104);
    m.cpu.registers.pc = 0x100;
    m.cartridge.rom[0x100] = 0xc7;
    m.cartridge.rom[0] = 0xc9;
    assert!(d.step(&mut m, Action::Next).is_empty());
    d.run_frame(&mut m).unwrap();
    assert_eq!(m.cpu.registers.pc, 0x101);
}

#[test]
fn generated_rom_dwarf_breakpoint_stack_scopes_and_variables() {
    let (mut d, mut m) =
        source_machine("SECTION \"Entry\", ROM0[$100]\nEntry::\n ld a,$42\n nop\n");
    let r = request(
        &mut d,
        &mut m,
        "setBreakpoints",
        json!({"source":{"path":"source.asm"},"breakpoints":[{"line":4}]}),
    )
    .response;
    assert_eq!(r["body"]["breakpoints"][0]["verified"], true);
    assert_eq!(r["body"]["breakpoints"][0]["line"], 4);
    d.run_frame(&mut m).unwrap();
    assert_eq!(m.cpu.registers.pc, 0x102);
    let stack = request(&mut d, &mut m, "stackTrace", json!({})).response;
    assert_eq!(stack["body"]["stackFrames"][0]["line"], 4);
    assert_eq!(
        stack["body"]["stackFrames"][0]["source"]["path"],
        "source.asm"
    );
    let vars = request(&mut d, &mut m, "variables", json!({"variablesReference":1})).response;
    let regs = vars["body"]["variables"].as_array().unwrap();
    assert_eq!(
        regs.iter().find(|v| v["name"] == "PC").unwrap()["value"],
        "$0102"
    );
    assert_eq!(
        regs.iter().find(|v| v["name"] == "A").unwrap()["value"],
        "$42"
    );
    let vars = request(&mut d, &mut m, "variables", json!({"variablesReference":4})).response;
    assert_eq!(vars["body"]["variables"][0]["value"], "$00:$0102");
    let vars = request(&mut d, &mut m, "variables", json!({"variablesReference":3})).response;
    assert!(
        vars["body"]["variables"]
            .as_array()
            .unwrap()
            .iter()
            .any(|v| v["name"] == "Entry")
    );
    m.cpu.registers.pc = 0x101;
    let interior = request(&mut d, &mut m, "stackTrace", json!({})).response;
    assert_eq!(interior["body"]["stackFrames"][0]["line"], 3);
    assert_eq!(
        interior["body"]["stackFrames"][0]["source"]["path"],
        "source.asm"
    );
}

#[test]
fn dwarf_banked_ranges_signed_constants_and_wide_bank_rejection() {
    let result = linked(
        "VALUE EQU -123\nSECTION \"one\", ROMX[$4000], BANK[1]\nOne:: ds 600\nSECTION \"two\", ROMX[$4000], BANK[2]\nTwo:: nop\n",
    );
    let mut d = Debugger::default();
    d.debug_info.load(&result.dwarf().unwrap()).unwrap();
    assert_eq!(d.debug_info.symbol("One").unwrap().bank, Some(1));
    assert_eq!(d.debug_info.symbol("Two").unwrap().bank, Some(2));
    assert_eq!(d.debug_info.line(1, 0x4257).unwrap().line, 3);
    assert!(d.debug_info.line(1, 0x4258).is_none());
    assert_eq!(d.debug_info.line(2, 0x4000).unwrap().line, 5);
    assert_eq!(d.debug_info.symbol("VALUE").unwrap().value, -123);
    let mut m = Machine::load_rom(&result.rom).unwrap();
    let response = request(&mut d, &mut m, "evaluate", json!({"expression":"VALUE"})).response;
    assert_eq!(response["body"]["result"], "$FFFFFF85 (-123)");
    assert!(response["body"]["memoryReference"].is_null());
    let symbols = request(&mut d, &mut m, "variables", json!({"variablesReference":3})).response;
    let constant = symbols["body"]["variables"]
        .as_array()
        .unwrap()
        .iter()
        .find(|v| v["name"] == "VALUE")
        .unwrap();
    assert_eq!(constant["value"], "-123");
    assert_eq!(
        request(&mut d, &mut m, "variables", json!({"variablesReference":4})).response["success"],
        true
    );
    assert_eq!(
        request(
            &mut d,
            &mut m,
            "setFunctionBreakpoints",
            json!({"breakpoints":[{"name":"VALUE"}]})
        )
        .response["body"]["breakpoints"][0]["verified"],
        false
    );
    assert!(d.debug_info.load(&[1, 2, 3]).is_err());
    assert!(d.debug_info.symbol("One").is_some());
    assert_eq!(d.debug_info.line(1, 0x4257).unwrap().line, 3);
    assert_eq!(d.debug_info.line(2, 0x4000).unwrap().line, 5);
    let wide = linked("SECTION \"wide\", ROMX[$4000], BANK[256]\nWide:: nop\n");
    let bytes = wide.dwarf().unwrap();
    assert_eq!(
        koh_link::read_dwarf(&bytes)
            .unwrap()
            .symbols
            .iter()
            .find(|s| s.name == "Wide")
            .unwrap()
            .bank,
        Some(256)
    );
    let error = d.debug_info.load(&bytes).unwrap_err();
    assert!(error.contains("256") && error.contains("255"));
    d.debug_info.load(&[]).unwrap();
    assert!(d.debug_info.symbols.is_empty());
    assert!(d.debug_info.lines.is_empty());
}

#[test]
fn dwarf_definition_and_single_600_byte_range_preserved() {
    let source = format!(
        "{}SECTION \"code\", ROM0[$150]\nmain:: ld hl,$1234",
        "\n".repeat(40)
    );
    let object = koh_asm::assemble(&source, "src/main.asm", false).unwrap();
    let result = koh_link::link(&[object], &Default::default()).unwrap();
    let data = koh_link::read_dwarf(&result.dwarf().unwrap()).unwrap();
    let sym = data.symbols.iter().find(|s| s.name == "main").unwrap();
    let source = sym.source.as_ref().unwrap();
    assert_eq!((&*source.file, source.line), ("src/main.asm", 42));
    assert_eq!(
        data.lines
            .iter()
            .find(|l| l.address == 0x150)
            .unwrap()
            .count,
        3
    );
    use koh_objects::{Line, Object, Section};
    let object = Object {
        sections: vec![Section {
            name: "range".into(),
            address: Some(0x200),
            size: 600,
            data: vec![0; 600],
            lines: vec![Line {
                offset: 0,
                count: 600,
                file: "range.asm".into(),
                line: 42,
                expansion: vec![],
            }],
            ..Default::default()
        }],
        ..Default::default()
    };
    let result = koh_link::link(&[object], &Default::default()).unwrap();
    let data = koh_link::read_dwarf(&result.dwarf().unwrap()).unwrap();
    assert_eq!(data.lines[0].count, 600);
    let mut d = Debugger::default();
    d.debug_info.load(&result.dwarf().unwrap()).unwrap();
    assert_eq!(d.debug_info.line(0, 0x457).unwrap().line, 42);
    assert!(d.debug_info.line(0, 0x458).is_none());
}

#[test]
fn stack_source_inside_multibyte_range_matches_original_fixture() {
    let object = koh_asm::assemble(
        "SECTION \"Entry\", ROM0[$0100]\n ld a,$42\n",
        "range.asm",
        false,
    )
    .unwrap();
    let linked = koh_link::link(&[object], &Default::default()).unwrap();
    let mut d = Debugger::default();
    d.debug_info.load(&linked.dwarf().unwrap()).unwrap();
    let mut m = Machine::load_rom(&linked.rom).unwrap();
    m.cpu.registers.pc = 0x0101;
    let response = request(&mut d, &mut m, "stackTrace", json!({"threadId":1})).response;
    let frame = &response["body"]["stackFrames"][0];
    assert_eq!(frame["line"], 2);
    assert_eq!(frame["source"]["path"], "range.asm");
}

#[test]
fn managed_oracle_disassembles_all_512_encodings() {
    let expected: Value = serde_json::from_str(include_str!("../tests/disassemble.json")).unwrap();
    let mut m = machine(&[0]);
    for case in expected.as_array().unwrap() {
        let op = case["op"].as_u64().unwrap() as u8;
        let bytes = if case["prefix"] == 0 {
            [op, 0xfe, 0x12]
        } else {
            [0xcb, op, 0x12]
        };
        m.cartridge.rom[..3].copy_from_slice(&bytes);
        let (text, length) = disassemble::decode(&m, 0);
        assert_eq!(text, case["text"], "opcode {bytes:02X?}");
        assert_eq!(length as u64, case["length"].as_u64().unwrap());
    }
}

#[test]
fn managed_oracle_handler_responses_match() {
    let cases: Value = serde_json::from_str(include_str!("../tests/responses.json")).unwrap();
    let mut m = machine(&[0x3e, 0x42, 0x18, 0xfc]);
    let mut d = Debugger::default();
    for case in cases.as_array().unwrap() {
        let mut actual = d.handle(&case["request"], &mut m).response;
        let mut expected = case["response"].clone();
        actual.as_object_mut().unwrap().remove("seq");
        expected.as_object_mut().unwrap().remove("seq");
        assert_eq!(actual, expected, "command {}", case["request"]["command"]);
    }
}

#[test]
fn native_unwind_metadata_recovers_far_caller_and_rejects_corruption() {
    let (mut d, mut m) = source_machine(
        "SECTION \"stubs\", ROM0[$100]\n__koh_far_return:: nop\nSECTION \"callee\", ROM0[$150]\n__koh_unwind_v1_b_0004_active:: nop\nSECTION \"caller\", ROM0[$200]\nCaller:: nop\n",
    );
    m.cpu.registers.pc = 0x150;
    m.cpu.registers.sp = 0xc100;
    for (a, v) in [
        (0xc104, 0),
        (0xc105, 1),
        (0xc106, 0),
        (0xc107, 2),
        (0xc108, 0),
        (0xc109, 0),
    ] {
        m.write_debug(a, v);
    }
    let stack = d.stack_trace(&m);
    assert_eq!(stack["totalFrames"], 2);
    assert_eq!(
        stack["stackFrames"][1]["instructionPointerReference"],
        "0x0200"
    );
    assert_eq!(stack["stackFrames"][1]["line"], 6);
    assert!(
        d.variables(&m, 3)
            .iter()
            .all(|v| !v["name"].as_str().unwrap().starts_with("__koh_unwind_"))
    );
    assert!(
        d.variables(&m, 4)
            .iter()
            .all(|v| !v["value"].as_str().unwrap().starts_with("__koh_unwind_"))
    );
    m.write_debug(0xc109, 1);
    assert_eq!(d.stack_trace(&m)["totalFrames"], 1);
    m.write_debug(0xc109, 0);
    m.write_debug(0xc104, 1);
    assert_eq!(d.stack_trace(&m)["totalFrames"], 1);
    m.write_debug(0xc104, 0);
    for marker in [
        "__koh_unwind_v2_b_0004_unknown",
        "__koh_unwind_v1_b_FFFF_invalid",
        "__koh_unwind_v1_b_0003_conflict",
    ] {
        d.debug_info.symbols.push(koh_link::LinkedSymbol {
            name: marker.into(),
            kind: 0,
            value: 0x150,
            bank: Some(0),
            source: None,
        });
        assert_eq!(d.stack_trace(&m)["totalFrames"], 1);
        d.debug_info.symbols.pop();
    }
}

#[test]
fn debugger_preserves_existing_memory_hook_across_watch_and_single_step() {
    struct Count(std::sync::Arc<std::sync::atomic::AtomicUsize>);
    impl MemoryHook for Count {
        fn on_read(&mut self, _: u16, _: u8, _: u16, _: u8, _: i32, _: PpuMode) {
            self.0.fetch_add(1, Ordering::Relaxed);
        }
        fn on_write(&mut self, _: u16, _: u8, _: u16, _: u8, _: i32, _: PpuMode) {
            self.0.fetch_add(1, Ordering::Relaxed);
        }
    }
    let count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let mut m = machine(&[0, 0, 0x18, 0xfe]);
    m.hook = Some(Box::new(Count(count.clone())));
    let mut d = Debugger::default();
    d.step(&mut m, Action::StepIn);
    assert_eq!(count.load(Ordering::Relaxed), 1);
    m.step_instruction();
    assert_eq!(count.load(Ordering::Relaxed), 2);
    assert!(d.run_frame(&mut m).is_none());
    assert!(count.load(Ordering::Relaxed) > 2);
    let prior = count.load(Ordering::Relaxed);
    m.step_instruction();
    assert!(count.load(Ordering::Relaxed) > prior);
}

#[test]
fn malformed_requests_are_recoverable_and_do_not_mutate_memory_or_breakpoints() {
    let mut m = machine(&[0]);
    let mut d = Debugger::default();
    for request in [
        Value::Null,
        json!({"seq":"bad","command":"continue"}),
        json!({"seq":1,"command":"writeMemory","arguments":{"memoryReference":"$C100","data":"qrvM","offset":"bad"}}),
        json!({"seq":1,"command":"setInstructionBreakpoints","arguments":{"breakpoints":[{"instructionReference":"0100"},{"instructionReference":12}]}}),
    ] {
        let reply = d.handle(&request, &mut m);
        assert_eq!(reply.response["success"], false);
        assert_eq!(reply.action, Action::None);
        assert!(reply.events.is_empty());
        assert!(d.breakpoints.is_empty());
        assert_eq!(m.read_debug(0xc100), 255);
    }
    assert_eq!(
        request(&mut d, &mut m, "evaluate", json!({"expression":"42"})).response["success"],
        true
    );
}

#[test]
fn breakpoint_condition_memory_reads_observe_bus_lock_and_read_watchpoints() {
    let mut m = machine(&[0x18, 0xfe]);
    let mut d = Debugger::default();
    m.cpu.registers.set_hl(0xc100);
    request(
        &mut d,
        &mut m,
        "setDataBreakpoints",
        json!({"breakpoints":[{"dataId":"$C100","accessType":"read"}]}),
    );
    d.breakpoints.insert(
        (0, 0x100),
        Breakpoint {
            condition: Some("[HL] == 0".into()),
            ..Default::default()
        },
    );
    assert_eq!(
        d.run_frame(&mut m).unwrap()["body"]["reason"],
        "data breakpoint"
    );
    assert_eq!(m.cpu.total_tcycles, 12);
    m.oam_dma.is_bus_locking = true;
    assert!(expression::condition(&mut m, "[$C100] == 255"));
}

#[test]
fn symbol_name_replacement_preserves_address_aliases_and_scope_order() {
    let mut d = Debugger::default();
    let m = machine(&[0]);
    for (name, value) in [("First", 0x100), ("Second", 0x101), ("FIRST", 0x102)] {
        d.debug_info.symbols.push(koh_link::LinkedSymbol {
            name: name.into(),
            value,
            kind: 0,
            bank: Some(0),
            source: None,
        });
    }
    let all = d.debug_info.all_symbols();
    assert_eq!(all.len(), 2);
    assert_eq!(all[0].name, "FIRST");
    assert_eq!(all[1].name, "Second");
    assert_eq!(d.evaluate(&m, "first").unwrap()["result"], "$00:$0102");
    assert_eq!(d.variables(&m, 3).len(), 2);
    assert!(d.variables(&m, 4).iter().any(|v| v["value"] == "First"));
}

#[test]
fn empty_session_dap_initializes_loads_metadata_and_rejects_machine_inspection() {
    let mut d = Debugger::default();
    for command in [
        "initialize",
        "threads",
        "scopes",
        "launch",
        "configurationDone",
        "pause",
        "continue",
    ] {
        let reply = d.handle_empty(&json!({"seq":1,"command":command,"arguments":{}}));
        assert_eq!(reply.response["success"], true, "{command}");
    }
    let variables = d
        .handle_empty(&json!({"seq":1,"command":"variables","arguments":{"variablesReference":1}}));
    assert_eq!(variables.response["body"]["variables"], json!([]));
    let bp=d.handle_empty(&json!({"seq":1,"command":"setInstructionBreakpoints","arguments":{"breakpoints":[{"instructionReference":"4000"}]}}));
    assert_eq!(bp.response["body"]["breakpoints"][0]["verified"], true);
    assert!(d.breakpoints.contains_key(&(1, 0x4000)));
    for (command, args) in [
        ("stackTrace", json!({})),
        ("evaluate", json!({"expression":"A"})),
        ("readMemory", json!({"memoryReference":"0100","count":1})),
        (
            "writeMemory",
            json!({"memoryReference":"$C100","data":"AQ=="}),
        ),
        (
            "disassemble",
            json!({"memoryReference":"0100","instructionCount":1}),
        ),
    ] {
        let reply = d.handle_empty(&json!({"seq":1,"command":command,"arguments":args}));
        assert_eq!(reply.response["success"], false);
        assert!(
            reply.response["message"]
                .as_str()
                .unwrap()
                .contains("no active")
        );
    }
}

#[test]
fn ram_labels_evaluate_and_list_with_managed_kind_name() {
    let (mut d, mut m) = source_machine("SECTION \"data\", WRAM0[$C100]\nState:: ds 1\n");
    let reply = request(&mut d, &mut m, "evaluate", json!({"expression":"State"}));
    assert_eq!(reply.response["body"]["type"], "RamLabel");
    assert_eq!(reply.response["body"]["memoryReference"], "0xC100");
    assert_eq!(
        d.variables(&m, 3)
            .iter()
            .find(|v| v["name"] == "State")
            .unwrap()["type"],
        "RamLabel"
    );
}
