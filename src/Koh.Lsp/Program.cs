using StreamJsonRpc;

var stdIn = Console.OpenStandardInput();
var stdOut = Console.OpenStandardOutput();

var handler = new HeaderDelimitedMessageHandler(stdOut, stdIn);
var rpc = new JsonRpc(handler);

var server = new Koh.Lsp.KohLanguageServer(rpc);
rpc.AddLocalRpcTarget(server);
rpc.StartListening();

await rpc.Completion;
