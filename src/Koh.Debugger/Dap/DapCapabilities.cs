using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap;

public static class DapCapabilities
{
    /// <summary>Phase 1 capabilities per spec §8.7.</summary>
    public static Capabilities Phase1() => new()
    {
        SupportsConfigurationDoneRequest = true,
        SupportsFunctionBreakpoints = false,
        SupportsConditionalBreakpoints = false,
        SupportsHitConditionalBreakpoints = false,
        SupportsStepBack = false,
        SupportsSetVariable = false,
        SupportsReadMemoryRequest = false,
        SupportsWriteMemoryRequest = false,
        SupportsDisassembleRequest = false,
        SupportsSteppingGranularity = false,
        SupportsInstructionBreakpoints = false,
        SupportsExceptionInfoRequest = false,
    };
}
