using System.Collections.Immutable;
using SessionMemory.Clients.Claude;
using SessionMemory.Clients.Codex;
using SessionMemory.Clients.Copilot;
using SessionMemory.Clients.Synthetic;
using SessionMemory.Clients.VsCode;

namespace SessionMemory.Clients
{
    /// <summary>
    /// The clients this tool reads, and the only place that names the ISessionClient implementations. Adding a client means writing
    /// one class that implements ISessionClient and listing it here; Corpus, Finder and Watcher do not change.
    /// </summary>
    internal static class ClientRegistry
    {
        // The proof conversations, including the canary every index build must find; a client made of functions rather than a class.
        public static readonly ISessionClient Synthetic = new LambdaSessionClient(SyntheticTranscripts.FamilyName,
            context => SyntheticTranscripts.Files(context.Root), context => SyntheticTranscripts.Parse(context.Root));

        private static readonly ISessionClient Codex = new CodexClient();

        private static readonly ISessionClient Claude = new ClaudeClient();

        private static readonly ISessionClient Copilot = new CopilotClient();

        private static readonly ISessionClient VsCode = new VsCodeClient();

        // Every client, in the order the manifest lists their families and find hands out their work.
        public static readonly ImmutableArray<ISessionClient> All = [Synthetic, Codex, Claude, Copilot, VsCode];

        // The clients whose hosts announce the session running a command, in order of precedence when several do.
        public static readonly ImmutableArray<ISessionClient> SessionHosts = [Copilot, Codex, Claude];
    }
}
