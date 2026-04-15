using Koh.Debugger.Dap.Handlers;

namespace Koh.Debugger.Dap;

public static class HandlerRegistration
{
    public static void RegisterAll(
        DapDispatcher dispatcher,
        DebugSession session,
        Func<string, ReadOnlyMemory<byte>> loadFile)
    {
        var launchHandler = new LaunchHandler(session, loadFile);
        var continueHandler = new ContinueHandler(session);
        var pauseHandler = new PauseHandler(session);
        var terminateHandler = new TerminateHandler(session);
        var setBpHandler = new SetBreakpointsHandler(session);
        var variablesHandler = new VariablesHandler(session);
        var readMemoryHandler = new ReadMemoryHandler(session);
        var stepHandlers = new StepHandlers(session);
        var stackTraceHandler = new StackTraceHandler(session);
        var disassembleHandler = new DisassembleHandler(session);
        var evaluateHandler = new EvaluateHandler(session);
        var bpHandlers = new BreakpointHandlers(session);

        dispatcher.RegisterHandler("initialize", InitializeHandler.Handle);
        dispatcher.RegisterHandler("launch", launchHandler.Handle);
        dispatcher.RegisterHandler("configurationDone", ConfigurationDoneHandler.Handle);
        dispatcher.RegisterHandler("continue", continueHandler.Handle);
        dispatcher.RegisterHandler("pause", pauseHandler.Handle);
        dispatcher.RegisterHandler("terminate", terminateHandler.Handle);
        dispatcher.RegisterHandler("setBreakpoints", setBpHandler.Handle);
        dispatcher.RegisterHandler("scopes", ScopesHandler.Handle);
        dispatcher.RegisterHandler("variables", variablesHandler.Handle);
        dispatcher.RegisterHandler("exceptionInfo", ExceptionInfoHandler.Handle);
        dispatcher.RegisterHandler("readMemory", readMemoryHandler.Handle);
        dispatcher.RegisterHandler("next", stepHandlers.HandleNext);
        dispatcher.RegisterHandler("stepIn", stepHandlers.HandleStepIn);
        dispatcher.RegisterHandler("stepOut", stepHandlers.HandleStepOut);
        dispatcher.RegisterHandler("stackTrace", stackTraceHandler.Handle);
        dispatcher.RegisterHandler("disassemble", disassembleHandler.Handle);
        dispatcher.RegisterHandler("evaluate", evaluateHandler.Handle);
        dispatcher.RegisterHandler("setInstructionBreakpoints", bpHandlers.HandleSetInstructionBreakpoints);
        dispatcher.RegisterHandler("setFunctionBreakpoints", bpHandlers.HandleSetFunctionBreakpoints);
        dispatcher.RegisterHandler("breakpointLocations", bpHandlers.HandleBreakpointLocations);
    }
}
