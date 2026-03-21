using System;
using System.Threading.Tasks;
using EmmyLua.LanguageServer.Framework.Server;
using EmmyLua.LanguageServer.Framework.Protocol.Message.Initialize;

// Create a language server
var server = LanguageServer.From(Console.OpenStandardInput(), Console.OpenStandardOutput());

// Handle initialization
server.OnInitialize((request, serverInfo) =>
{
    serverInfo.Name = "My Language Server";
    serverInfo.Version = "1.0.0";
    return Task.CompletedTask;
});

// Handle initialized notification
server.OnInitialized(async (request) =>
{
    await server.Client.LogInfo("Server initialized!");
});

// Run the server
await server.Run();