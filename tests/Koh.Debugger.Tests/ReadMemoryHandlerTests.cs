using System.Text.Json;
using Koh.Debugger;
using Koh.Debugger.Dap;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Debugger.Tests;

public class ReadMemoryHandlerTests
{
    [Test]
    public async Task ReadMemory_Returns_Base64_Bytes()
    {
        var dispatcher = new DapDispatcher();
        var session = new DebugSession();
        var responses = new List<byte[]>();
        dispatcher.ResponseReady += data => responses.Add(data.ToArray());

        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        rom[0x0100] = 0xAA;
        rom[0x0101] = 0xBB;
        session.Launch(rom, Array.Empty<byte>(), Koh.Emulator.Core.HardwareMode.Dmg);

        HandlerRegistration.RegisterAll(dispatcher, session, _ => Array.Empty<byte>());

        var request = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["seq"] = 1,
            ["type"] = "request",
            ["command"] = "readMemory",
            ["arguments"] = new Dictionary<string, object?>
            {
                ["memoryReference"] = "0100",
                ["offset"] = 0,
                ["count"] = 2,
            },
        });

        dispatcher.HandleRequest(request);

        string data;
        using (var doc = JsonDocument.Parse(responses[0]))
        {
            var body = doc.RootElement.GetProperty("body");
            data = body.GetProperty("data").GetString() ?? "";
        }
        byte[] decoded = Convert.FromBase64String(data);

        await Assert.That(decoded.Length).IsEqualTo(2);
        await Assert.That(decoded[0]).IsEqualTo((byte)0xAA);
        await Assert.That(decoded[1]).IsEqualTo((byte)0xBB);
    }
}
