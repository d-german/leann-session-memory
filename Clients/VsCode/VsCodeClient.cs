using System.Collections.Immutable;
using static SessionMemory.SourceFiles;

namespace SessionMemory.Clients.VsCode
{
    /// <summary>
    /// The ISessionClient for VS Code Copilot Chat, which stores each chat as an edit log. All VS Code-specific code lives in this
    /// class: file locations (this file), parsing (VsCodeClient.Parse.cs, with the edit-log replay in VsCodeClient.Replay.cs) and
    /// find scanning (VsCodeClient.Find.cs).
    /// </summary>
    sealed internal partial class VsCodeClient : ISessionClient
    {
        private const string FamilyName = "vscode";

        public string Name => FamilyName;

        // VS Code does not tell the commands its chat runs which session runs them.
        public string? SessionVariable => null;

        public ImmutableArray<string> Sources(ClientContext context) => ChatFiles(CodeUser(context.Home));

        public FamilyResult Parse(ClientContext context) => ReadSessions(context.Home);

        public IEnumerable<Action<Finder.Scan>> FindWork(FindContext context) => ChatWork(context.Home, SessionsByFile(context.Documents));

        public static string CodeUser(string home) => Path.Combine(home, "AppData", "Roaming", "Code", "User");

        public static ImmutableArray<string> ChatFiles(string codeUser)
        {
            string[][] patterns =
            [
                ["workspaceStorage", "*", "chatSessions"],
                ["profiles", "*", "workspaceStorage", "*", "chatSessions"],
                ["globalStorage", "*", "chatSessions"],
                ["profiles", "*", "globalStorage", "*", "chatSessions"],
                ["globalStorage", "emptyWindowChatSessions"],
                ["profiles", "*", "globalStorage", "emptyWindowChatSessions"]
            ];
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in patterns)
            {
                List<string> roots = Directory.Exists(codeUser) ? [codeUser] : [];
                var directories = pattern.Aggregate(roots, (current, part) =>
                [
                    .. current.SelectMany(SubDirectories)
                        .Where(directory => part == "*" || Path.GetFileName(directory).Equals(part, StringComparison.OrdinalIgnoreCase))
                ]);
                foreach (var directory in directories) found.UnionWith(Files(directory, "*.jsonl"));
            }
            var files = found.ToList();
            files.Sort(ComparePaths);
            return [.. files];
        }

        // Python sorts Windows paths by their lower-cased components.
        private static int ComparePaths(string left, string right)
        {
            var leftParts = left.ToLowerInvariant().Split(Path.DirectorySeparatorChar);
            var rightParts = right.ToLowerInvariant().Split(Path.DirectorySeparatorChar);
            for (var index = 0; index < Math.Min(leftParts.Length, rightParts.Length); index++)
            {
                var order = string.CompareOrdinal(leftParts[index], rightParts[index]);
                if (order != 0) return order;
            }
            return leftParts.Length.CompareTo(rightParts.Length);
        }
    }
}
