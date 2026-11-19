mod disassemble;
pub mod expression;
mod inspect;
pub mod metadata;

use base64::{Engine as _, engine::general_purpose::STANDARD};
use koh_emulator::{
    machine::{Machine, MemoryHook},
    ppu::PpuMode,
};
use serde_json::{Value, json};
use std::{
    collections::{BTreeMap, BTreeSet},
    path::Path,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Action {
    #[default]
    None,
    Continue,
    Pause,
    StepIn,
    Next,
    StepOut,
    Disconnect,
}

pub struct Reply {
    pub response: Value,
    pub events: Vec<Value>,
    pub action: Action,
}

#[derive(Default)]
struct Breakpoint {
    condition: Option<String>,
    hits: i32,
    target: i32,
}

pub struct Debugger {
    pub debug_info: metadata::DebugInfo,
    breakpoints: BTreeMap<(u8, u16), Breakpoint>,
    one_shots: BTreeSet<(u8, u16)>,
    watch_reads: BTreeSet<u16>,
    watch_writes: BTreeSet<u16>,
    next_seq: i64,
    stop_on_entry: bool,
}

impl Default for Debugger {
    fn default() -> Self {
        Self {
            debug_info: Default::default(),
            breakpoints: Default::default(),
            one_shots: Default::default(),
            watch_reads: Default::default(),
            watch_writes: Default::default(),
            next_seq: 1,
            stop_on_entry: false,
        }
    }
}

type SharedMemoryHook = Arc<Mutex<Option<Box<dyn MemoryHook>>>>;

struct WatchHook {
    reads: BTreeSet<u16>,
    writes: BTreeSet<u16>,
    hit: Arc<AtomicBool>,
    previous: SharedMemoryHook,
}
impl MemoryHook for WatchHook {
    fn set_context(&mut self, context: koh_emulator::machine::MemoryContext) {
        if let Some(h) = self.previous.lock().unwrap().as_mut() {
            h.set_context(context);
        }
    }
    fn on_hblank(&mut self, context: koh_emulator::machine::MemoryContext, ly: u8, dot: i32) {
        if let Some(h) = self.previous.lock().unwrap().as_mut() {
            h.on_hblank(context, ly, dot);
        }
    }
    fn on_read(&mut self, a: u16, v: u8, pc: u16, ly: u8, dot: i32, mode: PpuMode) {
        if self.reads.contains(&a) {
            self.hit.store(true, Ordering::Relaxed);
        }
        if let Some(h) = self.previous.lock().unwrap().as_mut() {
            h.on_read(a, v, pc, ly, dot, mode);
        }
    }
    fn on_write(&mut self, a: u16, v: u8, pc: u16, ly: u8, dot: i32, mode: PpuMode) {
        if self.writes.contains(&a) {
            self.hit.store(true, Ordering::Relaxed);
        }
        if let Some(h) = self.previous.lock().unwrap().as_mut() {
            h.on_write(a, v, pc, ly, dot, mode);
        }
    }
}

fn string<'a>(value: &'a Value, name: &str) -> Result<&'a str, String> {
    match value.get(name) {
        None => Ok(""),
        Some(v) => v.as_str().ok_or_else(|| format!("invalid {name}")),
    }
}
fn integer(value: &Value, name: &str) -> Result<i32, String> {
    match value.get(name) {
        None => Ok(0),
        Some(v) => v
            .as_i64()
            .and_then(|v| i32::try_from(v).ok())
            .ok_or_else(|| format!("invalid {name}")),
    }
}
fn array<'a>(args: &'a Value, name: &str) -> Result<&'a [Value], String> {
    match args.get(name) {
        None | Some(Value::Null) => Ok(&[]),
        Some(Value::Array(v)) if v.iter().all(Value::is_object) => Ok(v),
        _ => Err(format!("invalid {name}")),
    }
}
fn source(args: &Value) -> &str {
    args["source"]["path"]
        .as_str()
        .or_else(|| args["source"]["name"].as_str())
        .unwrap_or("")
}
fn breakpoint_body(mut results: Vec<Value>) -> Value {
    for result in &mut results {
        for key in ["id", "line", "source", "message", "instructionReference"] {
            result
                .as_object_mut()
                .unwrap()
                .entry(key)
                .or_insert(Value::Null);
        }
        if let Some(source) = result["source"].as_object_mut() {
            source.entry("name").or_insert(Value::Null);
            source.entry("path").or_insert(Value::Null);
        }
    }
    json!({"breakpoints":results})
}
pub fn bank(machine: &Machine, pc: u16) -> u8 {
    if pc >= 0x4000 {
        machine.cartridge.current_rom_bank()
    } else {
        0
    }
}

impl Debugger {
    pub fn load_debug_info(&mut self, path: &Path) -> Result<(), String> {
        self.debug_info.load_path(path)
    }
    pub fn clear_debug_info(&mut self) {
        self.debug_info = Default::default();
    }
    fn sequence(&mut self) -> i64 {
        let seq = self.next_seq;
        self.next_seq = self.next_seq.wrapping_add(1);
        seq
    }
    fn event(&mut self, name: &str, body: Value) -> Value {
        json!({"seq":self.sequence(),"type":"event","event":name,"body":body})
    }
    fn stopped(&mut self, reason: &str) -> Value {
        self.event(
            "stopped",
            json!({"reason":reason,"threadId":1,"allThreadsStopped":true}),
        )
    }
    pub fn handle(&mut self, request: &Value, machine: &mut Machine) -> Reply {
        self.handle_optional(request, Some(machine))
    }
    pub fn handle_empty(&mut self, request: &Value) -> Reply {
        self.handle_optional(request, None)
    }
    fn handle_optional(&mut self, request: &Value, machine: Option<&mut Machine>) -> Reply {
        let seq = self.sequence();
        let command = request["command"].as_str().unwrap_or("");
        let mut events = Vec::new();
        let mut action = Action::None;
        let result = if !request.is_object()
            || !request["seq"]
                .as_i64()
                .is_some_and(|s| i32::try_from(s).is_ok())
            || !request["command"].is_string()
        {
            Err("invalid request".into())
        } else {
            self.dispatch(
                command,
                &request["arguments"],
                machine,
                &mut action,
                &mut events,
            )
        };
        let mut response = json!({"seq":seq,"type":"response","request_seq":request["seq"].as_i64().unwrap_or(0),"command":command,"success":result.is_ok(),"message":null,"body":null});
        match result {
            Ok(body) => {
                response["body"] = body;
            }
            Err(error) => {
                response["message"] = error.into();
                action = Action::None;
                events.clear();
            }
        }
        Reply {
            response,
            events,
            action,
        }
    }
    fn dispatch(
        &mut self,
        command: &str,
        args: &Value,
        m: Option<&mut Machine>,
        action: &mut Action,
        events: &mut Vec<Value>,
    ) -> Result<Value, String> {
        let requires_args = matches!(
            command,
            "evaluate"
                | "readMemory"
                | "writeMemory"
                | "variables"
                | "disassemble"
                | "setBreakpoints"
                | "setInstructionBreakpoints"
                | "setFunctionBreakpoints"
                | "breakpointLocations"
                | "dataBreakpointInfo"
                | "setDataBreakpoints"
        );
        if requires_args && !args.is_object() {
            return Err(format!("{command}: missing args"));
        }
        match command {
            "initialize" => {
                events.push(self.event("initialized", Value::Null));
                Ok(
                    json!({"supportsConfigurationDoneRequest":true,"supportsFunctionBreakpoints":true,
                    "supportsConditionalBreakpoints":true,"supportsHitConditionalBreakpoints":true,
                    "supportsStepBack":false,"supportsSetVariable":false,"supportsReadMemoryRequest":true,
                    "supportsWriteMemoryRequest":true,"supportsDataBreakpoints":true,"supportsDisassembleRequest":true,
                    "supportsSteppingGranularity":false,"supportsInstructionBreakpoints":true,"supportsExceptionInfoRequest":false,
                    "supportsBreakpointLocationsRequest":true,"supportsEvaluateForHovers":true}),
                )
            }
            "launch" => {
                self.stop_on_entry = args["stopOnEntry"] == true;
                if let Some(path) = args["debugInfo"].as_str().filter(|p| !p.is_empty())
                    && let Err(error) = self.load_debug_info(Path::new(path))
                {
                    eprintln!("[koh-dap] failed to load .elf ({path}): {error}");
                }
                Ok(Value::Null)
            }
            "configurationDone" => {
                if self.stop_on_entry {
                    *action = Action::Pause;
                    events.push(self.stopped("entry"));
                } else {
                    *action = Action::Continue;
                }
                Ok(Value::Null)
            }
            "threads" => Ok(json!({"threads":[{"id":1,"name":"CPU"}]})),
            "continue" => {
                *action = Action::Continue;
                Ok(json!({"allThreadsContinued":true}))
            }
            "pause" => {
                *action = Action::Pause;
                events.push(self.stopped("pause"));
                Ok(Value::Null)
            }
            "disconnect" | "terminate" => {
                *action = Action::Disconnect;
                Ok(Value::Null)
            }
            "stepIn" => {
                *action = Action::StepIn;
                Ok(Value::Null)
            }
            "next" => {
                *action = Action::Next;
                Ok(Value::Null)
            }
            "stepOut" => {
                *action = Action::StepOut;
                Ok(Value::Null)
            }
            "exceptionInfo" => Ok(Value::Null),
            "scopes" => Ok(json!({"scopes":[
                {"name":"Registers","variablesReference":1,"expensive":false},
                {"name":"Hardware","variablesReference":2,"expensive":false},
                {"name":"Symbols","variablesReference":3,"expensive":true},
                {"name":"Source Context","variablesReference":4,"expensive":false}]})),
            "variables" => {
                let reference = integer(args, "variablesReference")?;
                Ok(
                    json!({"variables":m.as_deref().map_or_else(Vec::new,|m|self.variables(m,reference))}),
                )
            }
            "stackTrace" => Ok(self.stack_trace(m.ok_or("stackTrace: no active session")?)),
            "evaluate" => {
                let text = string(args, "expression")?;
                if text.trim().is_empty() {
                    return Err("evaluate: empty expression".into());
                }
                self.evaluate(m.ok_or("evaluate: no active session")?, text)
            }
            "readMemory" => {
                let m = m.ok_or("readMemory: no active session")?;
                let reference = string(args, "memoryReference")?;
                let base = expression::hex_address(reference, false)
                    .ok_or_else(|| format!("readMemory: invalid memoryReference '{reference}'"))?;
                let start = base.wrapping_add(integer(args, "offset")? as u16);
                let count = integer(args, "count")?.clamp(0, 0x10000 - start as i32) as usize;
                let bytes: Vec<_> = (0..count)
                    .map(|i| m.read_debug(start.wrapping_add(i as u16)))
                    .collect();
                Ok(
                    json!({"address":format!("0x{start:04X}"),"unreadableBytes":0,"data":STANDARD.encode(bytes)}),
                )
            }
            "writeMemory" => {
                let m = m.ok_or("writeMemory: no active debug session")?;
                let reference = string(args, "memoryReference")?;
                let base = expression::address(reference)
                    .ok_or_else(|| format!("writeMemory: invalid memoryReference '{reference}'"))?;
                let data = string(args, "data")?;
                let compact: String = data.chars().filter(|c| !c.is_whitespace()).collect();
                let bytes = STANDARD
                    .decode(compact)
                    .map_err(|_| "writeMemory: data is not valid base64".to_string())?;
                let offset = integer(args, "offset")?;
                let mut written = 0;
                for (i, value) in bytes.iter().enumerate() {
                    if !m.write_debug(
                        base.wrapping_add(offset as u16).wrapping_add(i as u16),
                        *value,
                    ) {
                        break;
                    }
                    written += 1;
                }
                if !bytes.is_empty() && written == 0 {
                    return Err("writeMemory: write rejected (emulator must be paused; check memory region is writable)".into());
                }
                Ok(json!({"offset":offset,"bytesWritten":written}))
            }
            "disassemble" => {
                let m = m.ok_or("disassemble: no active session")?;
                let reference = string(args, "memoryReference")?;
                let start = expression::hex_address(reference, false)
                    .ok_or_else(|| format!("disassemble: invalid memoryReference '{reference}'"))?;
                let mut pc = start.wrapping_add(integer(args, "offset")? as u16);
                let count = integer(args, "instructionCount")?.clamp(1, 512);
                let mut instructions = Vec::new();
                for _ in 0..count {
                    let (instruction, length) = disassemble::decode(m, pc);
                    let bytes = (0..length)
                        .map(|i| format!("{:02X}", m.read_debug(pc.wrapping_add(i))))
                        .collect::<Vec<_>>()
                        .join(" ");
                    instructions.push(json!({"address":format!("0x{pc:04X}"),"instructionBytes":bytes,"instruction":instruction}));
                    pc = pc.wrapping_add(length);
                }
                Ok(json!({"instructions":instructions}))
            }
            "setBreakpoints" => {
                if args.get("source").is_some_and(|source| !source.is_object()) {
                    return Err("invalid source".into());
                }
                let bps = array(args, "breakpoints")?;
                let file = source(args);
                let mut installed = BTreeMap::new();
                let mut results = Vec::new();
                for bp in bps {
                    let line = integer(bp, "line")?;
                    let addresses: Vec<_> = self.debug_info.addresses(file, line as u32).collect();
                    let condition = match bp.get("condition") {
                        None | Some(Value::Null) => None,
                        Some(v) => Some(v.as_str().ok_or("invalid condition")?.to_owned()),
                    };
                    let hit = match bp.get("hitCondition") {
                        None | Some(Value::Null) => "",
                        Some(v) => v.as_str().ok_or("invalid hitCondition")?,
                    }
                    .trim();
                    let target = hit
                        .find(|c: char| c.is_ascii_digit())
                        .and_then(|i| hit[i..].parse::<i32>().ok())
                        .unwrap_or(0);
                    for addr in &addresses {
                        installed.insert(
                            *addr,
                            Breakpoint {
                                condition: condition.clone(),
                                hits: 0,
                                target,
                            },
                        );
                    }
                    let mut result = json!({"verified":!addresses.is_empty(),"line":line,"source":args["source"]});
                    if addresses.is_empty() {
                        result["message"] = "no code at this line".into();
                    }
                    results.push(result);
                }
                self.breakpoints = installed;
                Ok(breakpoint_body(results))
            }
            "setInstructionBreakpoints" => {
                let mut installed = BTreeMap::new();
                let mut results = Vec::new();
                for bp in array(args, "breakpoints")? {
                    let reference = string(bp, "instructionReference")?;
                    if let Some(addr) = expression::hex_address(reference, true) {
                        let addr = addr.wrapping_add(integer(bp, "offset")? as u16);
                        installed.insert(
                            (
                                m.as_deref()
                                    .map_or(if addr >= 0x4000 { 1 } else { 0 }, |m| bank(m, addr)),
                                addr,
                            ),
                            Breakpoint::default(),
                        );
                        results.push(
                            json!({"verified":true,"instructionReference":format!("0x{addr:04X}")}),
                        );
                    } else {
                        results.push(json!({"verified":false,"message":format!("invalid instructionReference '{reference}'")}));
                    }
                }
                self.breakpoints = installed;
                Ok(breakpoint_body(results))
            }
            "setFunctionBreakpoints" => {
                let mut installed = BTreeMap::new();
                let mut results = Vec::new();
                for bp in array(args, "breakpoints")? {
                    let name = string(bp, "name")?;
                    if let Some(s) = self.debug_info.symbol(name).filter(|s| s.kind == 0) {
                        let addr = s.value as u16;
                        installed.insert((s.bank.unwrap_or(0) as u8, addr), Breakpoint::default());
                        results.push(
                            json!({"verified":true,"instructionReference":format!("0x{addr:04X}")}),
                        );
                    } else {
                        results.push(
                            json!({"verified":false,"message":format!("unknown symbol '{name}'")}),
                        );
                    }
                }
                self.breakpoints = installed;
                Ok(breakpoint_body(results))
            }
            "breakpointLocations" => {
                let start = integer(args, "line")?;
                let end = if args["endLine"].is_null() {
                    start
                } else {
                    integer(args, "endLine")?
                };
                let lines: BTreeSet<u32> = self
                    .debug_info
                    .lines
                    .iter()
                    .filter(|l| {
                        l.file.eq_ignore_ascii_case(source(args))
                            && l.line as i64 >= start as i64
                            && l.line as i64 <= end as i64
                    })
                    .map(|l| l.line)
                    .collect();
                Ok(
                    json!({"breakpoints":lines.iter().map(|l|json!({"line":l,"column":null})).collect::<Vec<_>>()}),
                )
            }
            "dataBreakpointInfo" => {
                let addr = expression::address(string(args, "name")?);
                Ok(if let Some(addr) = addr {
                    json!({"dataId":format!("0x{addr:04X}"),"description":format!("memory at ${addr:04X}"),"accessTypes":["read","write","readWrite"],"canPersist":false})
                } else {
                    json!({"dataId":null,"description":"watchpoints are supported only on raw memory addresses (e.g. $C000)","accessTypes":["read","write","readWrite"],"canPersist":false})
                })
            }
            "setDataBreakpoints" => {
                let mut reads = BTreeSet::new();
                let mut writes = BTreeSet::new();
                let mut results = Vec::new();
                for bp in array(args, "breakpoints")? {
                    let id = string(bp, "dataId")?;
                    if let Some(addr) = expression::address(id) {
                        let access = match bp.get("accessType") {
                            None | Some(Value::Null) => "write",
                            Some(v) => v.as_str().ok_or("invalid accessType")?,
                        };
                        if matches!(access, "read" | "readWrite") {
                            reads.insert(addr);
                        }
                        if matches!(access, "write" | "readWrite") {
                            writes.insert(addr);
                        }
                        results.push(json!({"verified":true}));
                    } else {
                        results.push(
                            json!({"verified":false,"message":format!("invalid dataId '{id}'")}),
                        );
                    }
                }
                self.watch_reads = reads;
                self.watch_writes = writes;
                Ok(breakpoint_body(results))
            }
            _ => Err(format!("unsupported command '{command}'")),
        }
    }
    fn install_hook(&self, m: &mut Machine) -> Option<(Arc<AtomicBool>, SharedMemoryHook)> {
        if self.watch_reads.is_empty() && self.watch_writes.is_empty() {
            return None;
        }
        let hit = Arc::new(AtomicBool::new(false));
        let previous = Arc::new(Mutex::new(m.hook.take()));
        m.hook = Some(Box::new(WatchHook {
            reads: self.watch_reads.clone(),
            writes: self.watch_writes.clone(),
            hit: hit.clone(),
            previous: previous.clone(),
        }));
        Some((hit, previous))
    }
    fn remove_hook(m: &mut Machine, watch: Option<(Arc<AtomicBool>, SharedMemoryHook)>) -> bool {
        let Some((hit, previous)) = watch else {
            return false;
        };
        m.hook = previous.lock().unwrap().take();
        hit.load(Ordering::Relaxed)
    }
    fn breakpoint_reason(&mut self, m: &mut Machine) -> Option<&'static str> {
        let pc = m.cpu.registers.pc;
        let address = (bank(m, pc), pc);
        if self.one_shots.remove(&address) {
            return Some("step");
        }
        let state = self.breakpoints.get_mut(&address)?;
        state.hits = state.hits.wrapping_add(1);
        if state.target > 0 && state.hits < state.target {
            return None;
        }
        if state
            .condition
            .as_ref()
            .is_some_and(|c| !expression::condition(m, c))
        {
            return None;
        }
        Some("breakpoint")
    }
    pub fn run_frame(&mut self, m: &mut Machine) -> Option<Value> {
        let watch = self.install_hook(m);
        let hit = || {
            watch
                .as_ref()
                .is_some_and(|(h, _)| h.load(Ordering::Relaxed))
        };
        m.stop_requested = None;
        m.frame_system_ticks = 0;
        let mut reason = None;
        while m.frame_system_ticks < Machine::SYSTEM_TICKS_PER_FRAME {
            m.step_instruction();
            if hit() {
                reason = Some("data breakpoint");
                break;
            }
            if let Some(stop) = self.breakpoint_reason(m) {
                reason = Some(stop);
                break;
            }
            if hit() {
                reason = Some("data breakpoint");
                break;
            }
            if m.stop_requested.is_some() {
                reason = Some("paused");
                break;
            }
        }
        Self::remove_hook(m, watch);
        reason.map(|r| self.stopped(r))
    }
    pub fn step(&mut self, m: &mut Machine, action: Action) -> Vec<Value> {
        let pc = m.cpu.registers.pc;
        let target = match action {
            Action::StepOut => Some(u16::from_le_bytes([
                m.read_debug(m.cpu.registers.sp),
                m.read_debug(m.cpu.registers.sp.wrapping_add(1)),
            ])),
            Action::Next => {
                let (text, len) = disassemble::decode(m, pc);
                if text.starts_with("CALL") || text.starts_with("RST") {
                    Some(pc.wrapping_add(len))
                } else {
                    None
                }
            }
            _ => None,
        };
        if let Some(target) = target {
            self.one_shots.insert((bank(m, target), target));
            return Vec::new();
        }
        let watch = self.install_hook(m);
        m.step_instruction();
        vec![self.stopped(if Self::remove_hook(m, watch) {
            "data breakpoint"
        } else {
            "step"
        })]
    }
}

#[cfg(test)]
mod tests;
