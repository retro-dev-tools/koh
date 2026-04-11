using System.Text.Json;
using Koh.Debugger.Dap.Messages;
using Koh.Emulator.Core;

namespace Koh.Debugger.Dap.Handlers;

public sealed class LaunchHandler
{
    private readonly DebugSession _session;
    private readonly Func<string, ReadOnlyMemory<byte>> _loadFile;

    public LaunchHandler(DebugSession session, Func<string, ReadOnlyMemory<byte>> loadFile)
    {
        _session = session;
        _loadFile = loadFile;
    }

    public Response Handle(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.LaunchRequestArguments);
        if (args is null || string.IsNullOrEmpty(args.Program))
        {
            return new Response { Success = false, Message = "launch: missing 'program'" };
        }

        var rom = _loadFile(args.Program);
        var kdbgPath = args.DebugInfo ?? System.IO.Path.ChangeExtension(args.Program, ".kdbg");
        var kdbg = _loadFile(kdbgPath);

        HardwareMode mode = args.HardwareMode switch
        {
            "dmg" => HardwareMode.Dmg,
            "cgb" => HardwareMode.Cgb,
            _     => DetectFromHeader(rom.Span),
        };

        _session.Launch(rom, kdbg, mode);

        return new Response { Success = true };
    }

    private static HardwareMode DetectFromHeader(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < 0x150) return HardwareMode.Dmg;
        byte cgbByte = rom[0x143];
        return (cgbByte & 0x80) != 0 ? HardwareMode.Cgb : HardwareMode.Dmg;
    }
}
