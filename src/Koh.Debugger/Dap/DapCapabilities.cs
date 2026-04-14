using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap;

public static class DapCapabilities
{
    /// <summary>Phase 2 capabilities per spec §8.7.</summary>
    public static Capabilities Phase2() => new()
    {
        SupportsConfigurationDoneRequest = true,
        SupportsFunctionBreakpoints = false,
        SupportsConditionalBreakpoints = false,
        SupportsHitConditionalBreakpoints = false,
        SupportsStepBack = false,
        SupportsSetVariable = false,
        SupportsReadMemoryRequest = true,
        SupportsWriteMemoryRequest = false,
        SupportsDisassembleRequest = true,
        SupportsSteppingGranularity = false,
        SupportsInstructionBreakpoints = false,
        SupportsExceptionInfoRequest = false,
    };
}
