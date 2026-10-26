using Microsoft.Extensions.DependencyInjection;
using SessionMemory.Clients.Claude;
using SessionMemory.Clients.Codex;
using SessionMemory.Clients.Copilot;
using SessionMemory.Clients.Synthetic;
using SessionMemory.Clients.VsCode;

namespace SessionMemory.Clients
{
    /// <summary>
    /// Registers the clients this tool reads; the only place that names the ISessionClient implementations. Adding a client means writing one class
    /// that implements ISessionClient and adding one registration line here; the services that use the clients do not change.
    /// </summary>
    internal static class ClientRegistration
    {
        extension(IServiceCollection services)
        {
            /// <summary>
            /// Adds every client as a singleton ISessionClient. The clients resolve in this order: the manifest lists their families in it, find hands
            /// out their work in it, and the current session is read from their hosts' variables in it.
            /// </summary>
            public IServiceCollection AddSessionClients() =>
                services
                    // The proof conversations, including the canary every index build must find; a client made of functions rather than a class.
                    .AddSingleton<ISessionClient>(new LambdaSessionClient(SyntheticTranscripts.FamilyName,
                        context => SyntheticTranscripts.Files(context.Root), context => SyntheticTranscripts.Parse(context.Root)))
                    .AddSingleton<ISessionClient, CodexClient>()
                    .AddSingleton<ISessionClient, ClaudeClient>()
                    .AddSingleton<ISessionClient, CopilotClient>()
                    .AddSingleton<ISessionClient, VsCodeClient>();
        }
    }
}
