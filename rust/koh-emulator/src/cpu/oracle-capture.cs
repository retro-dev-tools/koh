using System.Text.Json;
using Koh.Emulator;
using Koh.Emulator.Cartridge;
using Koh.Emulator.Debug;
using Koh.Emulator.State;

Directory.CreateDirectory(args[0]);
using var output = File.CreateText(Path.Combine(args[0], "cpu.jsonl"));
int count = 0;
foreach (bool prefixed in new[] { false, true })
    for (int opcode = 0; opcode < 256; opcode++)
    for (int variant = 0; variant < 4; variant++)
    {
        var rom = new byte[32768];
        rom[0x100] = prefixed ? (byte)0xcb : (byte)opcode;
        rom[0x101] = prefixed ? (byte)opcode : (byte)0x20;
        rom[0x102] = 0xc0;
        var gb = new GameBoySystem(CartridgeFactory.Load(rom));
        gb.Registers.Pc = 0x100;
        gb.Registers.Sp = 0xdffe;
        gb.Registers.A = new byte[] { 0, 1, 0x7f, 0xff }[variant];
        gb.Registers.F = new byte[] { 0, 0x10, 0x80, 0xf0 }[variant];
        gb.Registers.BC = 0xc180;
        gb.Registers.DE = 0xc234;
        gb.Registers.HL = 0xc123;
        gb.DebugWriteByte(0xc123, 0x81);
        gb.DebugWriteByte(0xff80, 0x5a);
        gb.DebugWriteByte(0xc020, 0xa5);
        var hook = new TraceHook(() => gb.Cpu.TotalTCycles);
        gb.Mmu.Hook = hook;
        gb.StepInstruction();
        using var state = new MemoryStream();
        using (var writer = new StateWriter(state))
            gb.Cpu.WriteState(writer);
        output.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    prefixed,
                    opcode,
                    variant,
                    state = Convert.ToHexString(state.ToArray()).ToLowerInvariant(),
                    trace = hook.Events,
                }
            )
        );
        count++;
    }
Console.WriteLine($"Captured {count} C# CPU instruction traces");

sealed class TraceHook(Func<ulong> clock) : MemoryHook
{
    public List<object> Events { get; } = new();

    public override void OnRead(ushort address, byte value) =>
        Events.Add(
            new
            {
                kind = "read",
                address,
                value,
                ticks = clock(),
            }
        );

    public override void OnWrite(ushort address, byte value) =>
        Events.Add(
            new
            {
                kind = "write",
                address,
                value,
                ticks = clock(),
            }
        );
}
