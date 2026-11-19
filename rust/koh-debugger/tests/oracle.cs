using System.Text.Json;
using Koh.Debugger;
using Koh.Debugger.Dap;
using Koh.Emulator;
using Koh.Emulator.Cartridge;
using Koh.Opcodes;

var decodes = new List<object>();
for (int prefix = 0; prefix < 2; prefix++)
for (int op = 0; op < 256; op++)
{
    byte[] bytes = prefix == 0 ? [(byte)op, 0xFE, 0x12] : [0xCB, (byte)op, 0x12];
    var (text, length) = Sm83Disassembler.DecodeOne(address => bytes[address], 0);
    decodes.Add(
        new
        {
            prefix,
            op,
            text,
            length,
        }
    );
}
File.WriteAllText(Path.Combine(args[0], "disassemble.json"), JsonSerializer.Serialize(decodes));
var rom = new byte[0x8000];
rom[0x100] = 0x3E;
rom[0x101] = 0x42;
rom[0x102] = 0x18;
rom[0x103] = 0xFC;
var machine = new GameBoySystem(CartridgeFactory.Load(rom), HardwareMode.Dmg);
machine.Registers.Pc = 0x100;
machine.Registers.Sp = 0xFFFE;
var session = new DebugSession();
session.AdoptSystem(machine);
var dispatcher = new DapDispatcher();
HandlerRegistration.RegisterAll(dispatcher, session, _ => Array.Empty<byte>());
var cases = new List<object>();
JsonElement response = default;
dispatcher.ResponseReady += bytes => response = JsonDocument.Parse(bytes).RootElement.Clone();
int sequence = 0;
void Request(string command, object arguments)
{
    var request = new
    {
        seq = ++sequence,
        type = "request",
        command,
        arguments,
    };
    dispatcher.HandleRequest(JsonSerializer.SerializeToUtf8Bytes(request));
    cases.Add(new { request, response });
}
Request("initialize", new { });
Request("scopes", new { frameId = 0 });
foreach (int variablesReference in new[] { 1, 2, 3, 4, 99 })
    Request("variables", new { variablesReference });
foreach (string expression in new[] { "$100", "42", "A", "HL", "%1010", "-123", "unknown", "" })
    Request("evaluate", new { expression });
Request("readMemory", new { memoryReference = "0100", count = 4 });
Request(
    "writeMemory",
    new
    {
        memoryReference = "$C100",
        data = "qrvM",
        offset = 0,
    }
);
Request("readMemory", new { memoryReference = "0xC100", count = 3 });
Request("dataBreakpointInfo", new { name = "$C100" });
Request("dataBreakpointInfo", new { name = "symbol" });
Request(
    "setDataBreakpoints",
    new
    {
        breakpoints = new[]
        {
            new { dataId = "$C100", accessType = "write" },
            new { dataId = "bad", accessType = "read" },
        },
    }
);
Request(
    "setInstructionBreakpoints",
    new
    {
        breakpoints = new[]
        {
            new { instructionReference = "0x0100" },
            new { instructionReference = "bad-reference" },
        },
    }
);
Request("setFunctionBreakpoints", new { breakpoints = new[] { new { name = "unknown" } } });
Request(
    "setBreakpoints",
    new { source = new { path = "unknown.asm" }, breakpoints = new[] { new { line = 5 } } }
);
Request(
    "breakpointLocations",
    new
    {
        source = new { path = "unknown.asm" },
        line = 1,
        endLine = 5,
    }
);
Request("stackTrace", new { threadId = 1 });
Request("disassemble", new { memoryReference = "0100", instructionCount = 2 });
Request("unknown", new { });
File.WriteAllText(Path.Combine(args[0], "responses.json"), JsonSerializer.Serialize(cases));
