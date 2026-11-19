using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Koh.Emulator;
using Koh.Emulator.Cartridge;
using Koh.Emulator.Joypad;

namespace Koh.Emulator.App;

internal sealed class EngineSession(EmulatorLoop loop, BootRomResolver bootRoms)
{
    private string? _romPath;
    private int _probe = "deliberate CI probe compile error";
    private byte[] _framebuffer = [];
    private PaletteSnapshot? _palettes;
    private VramSnapshot? _vram;
    private MemorySnapshot? _memory;

    public void Load(string path, bool paused)
    {
        path = Path.GetFullPath(path);
        long length = new FileInfo(path).Length;
        if (length is < 0x150 or > 8 * 1024 * 1024)
            throw new InvalidDataException("ROM size must be between 336 bytes and 8 MiB");
        var system = new GameBoySystem(CartridgeFactory.Load(File.ReadAllBytes(path)));
        system.LoadBootRom(bootRoms.Resolve(system.Mode));
        loop.SetSystem(system, path);
        _romPath = path;
        loop.FastForward = false;
        if (paused)
            loop.Pause();
        else
            loop.Resume();
    }

    public async Task<int> RunAsync(TextReader input, Stream output, CancellationToken cancellation)
    {
        var buffer = new char[1];
        var line = new StringBuilder();
        while (
            await input.ReadAsync(buffer.AsMemory(), cancellation).AsTask().WaitAsync(cancellation)
            != 0
        )
        {
            if (buffer[0] != '\n')
            {
                if (line.Length == 16384)
                {
                    Console.Error.WriteLine("[koh-engine] command exceeded 16384 characters");
                    return 1;
                }
                line.Append(buffer[0]);
                continue;
            }
            long id = 0;
            EngineReply reply;
            bool quit = false;
            try
            {
                using var document = JsonDocument.Parse(
                    line.ToString(),
                    new JsonDocumentOptions { MaxDepth = 8 }
                );
                var request = document.RootElement;
                if (request.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Expected a command object");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in request.EnumerateObject())
                    if (!names.Add(property.Name))
                        throw new InvalidDataException("Duplicate command property");
                id = request.GetProperty("id").GetInt64();
                if (id < 0)
                    throw new InvalidDataException("Command id must be nonnegative");
                string command =
                    request.GetProperty("command").GetString()
                    ?? throw new InvalidDataException("Missing command");
                quit = command == "quit";
                var snapshot = loop.Execute(() => Handle(command, request));
                reply = new EngineReply(id, true, null, snapshot);
            }
            catch (Exception ex)
                when (ex
                        is JsonException
                            or IOException
                            or InvalidDataException
                            or ArgumentException
                            or InvalidOperationException
                            or KeyNotFoundException
                            or FormatException
                            or OverflowException
                            or UnauthorizedAccessException
                            or NotSupportedException
                )
            {
                reply = new EngineReply(id, false, ex.Message, null);
            }
            line.Clear();
            JsonSerializer.Serialize(output, reply, EngineJsonContext.Default.EngineReply);
            output.WriteByte((byte)'\n');
            await output.FlushAsync(cancellation);
            if (quit && reply.Ok)
                return 0;
        }
        if (line.Length != 0)
        {
            Console.Error.WriteLine("[koh-engine] incomplete command at EOF");
            return 1;
        }
        return 0;
    }

    private EngineSnapshot Handle(string command, JsonElement request)
    {
        bool debug = request.TryGetProperty("debug", out var showDebug) && showDebug.GetBoolean();
        ushort address = request.TryGetProperty("address", out var memoryAddress)
            ? memoryAddress.GetUInt16()
            : (ushort)0;
        switch (command)
        {
            case "snapshot":
                break;
            case "load":
                Load(
                    request.GetProperty("path").GetString() ?? "",
                    request.TryGetProperty("paused", out var paused) && paused.GetBoolean()
                );
                break;
            case "reset":
                Load(RequireRom(), loop.IsPaused);
                break;
            case "pause":
                loop.Pause();
                break;
            case "resume":
                RequireRom();
                loop.Resume();
                break;
            case "step":
                RequireRom();
                if (!loop.IsPaused)
                    throw new InvalidOperationException("Pause before stepping");
                loop.StepOne();
                break;
            case "fastForward":
                loop.FastForward = request.GetProperty("enabled").GetBoolean();
                break;
            case "joypad":
                RequireRom();
                var button = request.GetProperty("button").GetString() switch
                {
                    "Up" => JoypadButton.Up,
                    "Down" => JoypadButton.Down,
                    "Left" => JoypadButton.Left,
                    "Right" => JoypadButton.Right,
                    "A" => JoypadButton.A,
                    "B" => JoypadButton.B,
                    "Start" => JoypadButton.Start,
                    "Select" => JoypadButton.Select,
                    _ => throw new InvalidDataException("Unknown joypad button"),
                };
                bool down = request.GetProperty("down").GetBoolean();
                loop.Send(system =>
                {
                    if (down)
                        system.JoypadPress(button);
                    else
                        system.JoypadRelease(button);
                });
                break;
            case "releaseInput":
                loop.FastForward = false;
                loop.Send(system => system.JoypadRelease((JoypadButton)255));
                break;
            case "save":
                loop.SaveState(RequireRom() + ".state");
                break;
            case "restore":
                loop.LoadState(RequireRom() + ".state");
                break;
            case "quit":
                loop.Pause();
                break;
            default:
                throw new InvalidDataException("Unknown engine command");
        }
        var sys = loop.CurrentSystem;
        var frame = loop.CurrentFramebuffer;
        if (_framebuffer.Length != frame.Length)
            _framebuffer = new byte[frame.Length];
        frame.CopyTo(_framebuffer, 0);
        bool inspect = debug && sys is not null;
        // Reused across replies: RunAsync serializes each reply before handling the next command.
        if (inspect)
        {
            _palettes = PaletteSnapshot.From(sys!, _palettes);
            _vram = VramSnapshot.From(sys!, _vram);
            _memory = MemorySnapshot.From(sys!, address, _memory);
        }
        return new EngineSnapshot(
            1,
            _romPath,
            loop.FrameCount,
            loop.IsPaused,
            loop.FastForward,
            sys?.Mode.ToString(),
            sys is null ? null : CpuSnapshot.From(sys),
            _framebuffer,
            inspect ? _palettes : null,
            inspect ? _vram : null,
            inspect ? _memory : null
        );
    }

    private string RequireRom() =>
        _romPath ?? throw new InvalidOperationException("Open a ROM first");
}

internal sealed record EngineSnapshot(
    int ProtocolVersion,
    string? RomPath,
    long Frame,
    bool Paused,
    bool FastForward,
    string? Mode,
    CpuSnapshot? Cpu,
    byte[] Framebuffer,
    PaletteSnapshot? Palettes,
    VramSnapshot? Vram,
    MemorySnapshot? Memory
);

internal sealed record EngineReply(long Id, bool Ok, string? Error, EngineSnapshot? Snapshot);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EngineReply))]
internal partial class EngineJsonContext : JsonSerializerContext;
