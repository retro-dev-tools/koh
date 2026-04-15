using Microsoft.JSInterop;

namespace Koh.Emulator.App.Shell;

public sealed class RuntimeModeDetector
{
    private readonly IJSRuntime _js;
    public RuntimeModeDetector(IJSRuntime js) { _js = js; }

    public async Task<RuntimeMode> DetectAsync()
    {
        try
        {
            bool insideVsCode = await _js.InvokeAsync<bool>("kohRuntimeMode.isInsideVsCodeWebview");
            return insideVsCode ? RuntimeMode.Debug : RuntimeMode.Standalone;
        }
        catch
        {
            return RuntimeMode.Standalone;
        }
    }
}
