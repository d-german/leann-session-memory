using System.Collections.Immutable;
using System.Text.Json;

namespace SessionMemory.Clients.Claude
{
    // Searches the raw Claude Code transcripts for find, including tool inputs; tool results stay out.
    sealed internal partial class ClaudeClient
    {
        private static IEnumerable<Action<Finder.Scan>> TranscriptWork(string home) =>
            Transcripts(home).Select(path => (Action<Finder.Scan>)(scan => ScanFile(scan, path)));

        private static void ScanFile(Finder.Scan scan, string path)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var subagent = path.Split(Path.DirectorySeparatorChar).Any(part => part.Equals("subagents", StringComparison.OrdinalIgnoreCase));
            foreach (var line in Finder.Lines(path))
            {
                if (!scan.Mentions(line) || line.Contains("\"tool_result\"", StringComparison.Ordinal)) continue;
                using var json = Jsonl.Parse(line);
                if (json is null) continue;
                var record = (JsonElement?)json.RootElement;
                var sessionId = record.Get("sessionId").StrOr(stem);
                scan.Note("claude", subagent ? $"{sessionId}:subagent:{stem}" : sessionId, line, Finder.Utc(record.Get("timestamp").StrOr("")),
                    () => LineExcerpt(line, scan.Terms));
            }
        }

        // Parses its own document, so the deferred excerpt never depends on one disposed by the scanning loop.
        private static Finder.Snippet? LineExcerpt(string line, ImmutableArray<string> terms)
        {
            using var json = Jsonl.Parse(line);
            return json is null ? null : Finder.Excerpt(json.RootElement.TryGetProperty("message", out var message) ? message : json.RootElement, "", terms);
        }
    }
}
