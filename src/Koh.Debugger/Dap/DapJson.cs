using System.Text.Json.Serialization;
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap;

[JsonSerializable(typeof(ProtocolMessage))]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
[JsonSerializable(typeof(Event))]
[JsonSerializable(typeof(Capabilities))]
[JsonSerializable(typeof(InitializeRequestArguments))]
[JsonSerializable(typeof(LaunchRequestArguments))]
[JsonSerializable(typeof(ContinueArguments))]
[JsonSerializable(typeof(ContinueResponseBody))]
[JsonSerializable(typeof(PauseArguments))]
[JsonSerializable(typeof(ConfigurationDoneArguments))]
[JsonSerializable(typeof(TerminateArguments))]
[JsonSerializable(typeof(SetBreakpointsArguments))]
[JsonSerializable(typeof(SetBreakpointsResponseBody))]
[JsonSerializable(typeof(Breakpoint))]
[JsonSerializable(typeof(ScopesArguments))]
[JsonSerializable(typeof(ScopesResponseBody))]
[JsonSerializable(typeof(Scope))]
[JsonSerializable(typeof(VariablesArguments))]
[JsonSerializable(typeof(VariablesResponseBody))]
[JsonSerializable(typeof(Variable))]
[JsonSerializable(typeof(ReadMemoryArguments))]
[JsonSerializable(typeof(ReadMemoryResponseBody))]
[JsonSerializable(typeof(StackTraceArguments))]
[JsonSerializable(typeof(StackTraceResponseBody))]
[JsonSerializable(typeof(StackFrame))]
[JsonSerializable(typeof(DisassembleArguments))]
[JsonSerializable(typeof(DisassembleResponseBody))]
[JsonSerializable(typeof(DisassembledInstruction))]
public sealed partial class DapJsonContext : JsonSerializerContext
{
}
