using System.Collections.Immutable;
using static SessionMemory.SourceFiles;

namespace SessionMemory.Clients.Codex
{
    /// <summary>
    /// The ISessionClient for OpenAI Codex, which keeps its conversations in a SQLite history database, with JSONL rollouts as a
    /// fallback and thread names in a session index and a state database. All Codex-specific code lives in this class: file
    /// locations (this file), parsing (CodexClient.Parse.cs) and find scanning (CodexClient.Find.cs).
    /// </summary>
    sealed internal partial class CodexClient : ISessionClient
    {
        private const string FamilyName = "codex";

        private static readonly string[] Folders = ["sessions", "archived_sessions"];

        private static readonly string[] RolloutPatterns = ["*.jsonl", "*.jsonl.zst"];

        public string Name => FamilyName;

        // Codex 0.95 and later set it for the commands they run.
        public string SessionVariable => "CODEX_THREAD_ID";

        public ImmutableArray<string> Sources(ClientContext context) => WatchedFiles(context.Home);

        public FamilyResult Parse(ClientContext context) => ReadSessions(context.Home);

        public IEnumerable<Action<Finder.Scan>> FindWork(FindContext context) => HistoryWork(context.Home);

        private static string History(string home) => Path.Combine(home, ".codex", "thread_history_1.sqlite");

        private static string SessionIndex(string home) => Path.Combine(home, ".codex", "session_index.jsonl");

        // Codex compresses older rollouts to .jsonl.zst; some sessions survive only in that form.
        private static IEnumerable<string> Rollouts(string home) =>
            Folders
                .SelectMany(folder => RolloutPatterns.SelectMany(pattern => Rglob(Path.Combine(home, ".codex", folder), pattern)));

        // The version suffix (state_5) changes with Codex releases; the newest file is current.
        private static IEnumerable<string> States(string home) =>
            Directory.Exists(Path.Combine(home, ".codex"))
                ? Directory.EnumerateFiles(Path.Combine(home, ".codex"), "state_*.sqlite").Order(StringComparer.Ordinal).TakeLast(1)
                : [];

        private static ImmutableArray<string> WatchedFiles(string home) =>
            [.. DatabaseFiles(History(home)), .. Rollouts(home), SessionIndex(home), .. States(home).SelectMany(DatabaseFiles)];
    }
}
