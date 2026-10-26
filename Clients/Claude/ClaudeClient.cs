using System.Collections.Immutable;
using static SessionMemory.SourceFiles;

namespace SessionMemory.Clients.Claude
{
    /// <summary>
    /// The ISessionClient for Claude Code, which writes one JSONL file per session under ~/.claude/projects, and one per subagent
    /// run in a "subagents" folder. All Claude-specific code lives in this class: file locations (this file), parsing
    /// (ClaudeClient.Parse.cs) and find scanning (ClaudeClient.Find.cs).
    /// </summary>
    sealed internal partial class ClaudeClient : ISessionClient
    {
        private const string FamilyName = "claude";

        public string Name => FamilyName;

        // Claude Code 2.1.132 and later set it for the commands they run.
        public string SessionVariable => "CLAUDE_CODE_SESSION_ID";

        public ImmutableArray<string> Sources(ClientContext context) => [.. Transcripts(context.Home)];

        public FamilyResult Parse(ClientContext context) => ReadSessions(context.Home);

        public IEnumerable<Action<Finder.Scan>> FindWork(FindContext context) => TranscriptWork(context.Home);

        private static IEnumerable<string> Transcripts(string home) => Rglob(Path.Combine(home, ".claude", "projects"), "*.jsonl");
    }
}
