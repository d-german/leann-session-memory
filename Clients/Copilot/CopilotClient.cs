using System.Collections.Immutable;
using static SessionMemory.SourceFiles;

namespace SessionMemory.Clients.Copilot
{
    /// <summary>
    /// The ISessionClient for GitHub Copilot CLI and the Copilot desktop app. Each session has an events.jsonl log under
    /// ~/.copilot/session-state; older sessions survive only as turns in a session-store database. All Copilot-specific code
    /// lives in this class: file locations (this file), parsing (CopilotClient.Parse.cs) and find scanning (CopilotClient.Find.cs).
    /// </summary>
    sealed internal partial class CopilotClient : ISessionClient
    {
        private const string FamilyName = "copilot";

        public string Name => FamilyName;

        // GitHub Copilot CLI sets it for the commands it runs.
        public string SessionVariable => "COPILOT_AGENT_SESSION_ID";

        public ImmutableArray<string> Sources(ClientContext context) => WatchedFiles(context.Home);

        public FamilyResult Parse(ClientContext context) => ReadSessions(context.Home);

        public IEnumerable<Action<Finder.Scan>> FindWork(FindContext context) => EventWork(context.Home, context.Outputs);

        private static string StoreFolder(string home) => Path.Combine(home, ".copilot");

        private static ImmutableArray<string> WatchedFiles(string home) =>
        [
            .. DatabaseFiles(Path.Combine(StoreFolder(home), "data.db")),
            .. DatabaseFiles(Path.Combine(StoreFolder(home), "session-store.db")),
            .. EventFiles(home),
            .. EventFiles(home).Select(WorkspaceFile).Where(File.Exists)
        ];

        private static string WorkspaceFile(string eventFile) => Path.Combine(Path.GetDirectoryName(eventFile)!, "workspace.yaml");

        private static IEnumerable<string> EventFiles(string home)
        {
            var root = Path.Combine(StoreFolder(home), "session-state");
            return Directory.Exists(root)
                ? Directory.EnumerateDirectories(root).Select(directory => Path.Combine(directory, "events.jsonl")).Where(File.Exists)
                : [];
        }
    }
}
