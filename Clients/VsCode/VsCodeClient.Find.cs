using System.Globalization;
using System.Text.RegularExpressions;

namespace SessionMemory.Clients.VsCode
{
    // Searches the raw VS Code chat files for find; each line is one edit of the chat.
    sealed internal partial class VsCodeClient
    {
        // A chat file's session ID comes from its last capture, since the file name may differ from it.
        private static Dictionary<string, string> SessionsByFile(IReadOnlyDictionary<string, DocumentMeta> documents) =>
            documents.Values.Where(document => document.Family == FamilyName)
                .GroupBy(document => document.SourceRef, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().SessionId, StringComparer.OrdinalIgnoreCase);

        private static IEnumerable<Action<Finder.Scan>> ChatWork(string home, Dictionary<string, string> sessions) =>
            ChatFiles(CodeUser(home)).Select(path => (Action<Finder.Scan>)(scan =>
                ScanChat(scan, path, sessions.GetValueOrDefault(path) ?? Path.GetFileNameWithoutExtension(path))));

        private static void ScanChat(Finder.Scan scan, string path, string sessionId)
        {
            var modified = File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            foreach (var line in Finder.Lines(path))
            {
                if (!scan.Mentions(line)) continue;
                var match = Timestamp().Match(line);
                scan.Note("vscode-copilot", sessionId, line,
                    match.Success ? Finder.Utc(Transcript.IsoFromMs(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))) : modified,
                    () =>
                    {
                        using var json = Jsonl.Parse(line);
                        return json is null ? null : Finder.Excerpt(json.RootElement, "", scan.Terms);
                    });
            }
        }

        [GeneratedRegex("\"timestamp\":(\\d{13})", RegexOptions.Compiled)]
        private static partial Regex Timestamp();
    }
}
