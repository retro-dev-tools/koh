using System.Text.Json;
using Koh.Debugger.Dap.Messages;
using Koh.Emulator;

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
        var debugInfoPath = args.DebugInfo ?? System.IO.Path.ChangeExtension(args.Program, ".elf");
        var debug = _loadFile(debugInfoPath);

        HardwareMode? mode = args.HardwareMode switch
        {
            "dmg" => HardwareMode.Dmg,
            "cgb" => HardwareMode.Cgb,
            _ => null,
        };

        _session.Launch(rom, debug, mode);

        return new Response { Success = true };
    }
}
