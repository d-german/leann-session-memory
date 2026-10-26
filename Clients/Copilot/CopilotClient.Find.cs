using System.Collections.Immutable;
using System.Text.Json;

namespace SessionMemory.Clients.Copilot
{
    // Searches the raw Copilot event logs for find, including tool inputs, and tool results when they are asked for.
    sealed internal partial class CopilotClient
    {
        private const string ToolOutput = "tool.execution_complete";
        private static readonly string[] SearchedTypes = ["user.message", "assistant.message", "tool.execution_start"];

        private static IEnumerable<Action<Finder.Scan>> EventWork(string home, bool outputs) =>
            EventFiles(home).Select(path => (Action<Finder.Scan>)(scan => ScanEvents(scan, path, outputs)));

        private static void ScanEvents(Finder.Scan scan, string path, bool outputs)
        {
            var sessionId = Path.GetFileName(Path.GetDirectoryName(path)!);
            var selfCalls = outputs ? new HashSet<string>(StringComparer.Ordinal) : null;
            foreach (var line in Finder.Lines(path))
            {
                if (selfCalls is not null) RememberSelfCall(line, selfCalls);
                if (scan.Mentions(line)) NoteEvent(scan, sessionId, line, selfCalls);
            }
        }

        // Tool results count only when selfCalls is given, which is when they are asked for.
        private static void NoteEvent(Finder.Scan scan, string sessionId, string line, HashSet<string>? selfCalls)
        {
            using var json = Jsonl.Parse(line);
            var record = json?.RootElement;
            var type = record.Get("type").AsString();
            if (type == ToolOutput && selfCalls is not null) NoteOutput(scan, sessionId, record, selfCalls);
            else if (SearchedTypes.Contains(type))
                scan.Note("copilot", sessionId, line, Finder.Utc(record.Get("timestamp").StrOr("")),
                    () => record.Get("data") is { } data ? Finder.Excerpt(data, record.Get("type").Str(), scan.Terms) : null);
        }

        // Results of the memory tool's own runs repeat what it found in other sessions, so the calls that started them are remembered.
        private static void RememberSelfCall(string line, HashSet<string> selfCalls)
        {
            if (!line.Contains("\"tool.execution_start\"", StringComparison.Ordinal) || !Finder.Scan.IsSelfInvocation(line)) return;
            using var json = Jsonl.Parse(line);
            var record = json?.RootElement;
            if (record.Get("type").AsString() == "tool.execution_start" && record.Get("data").Get("toolCallId").AsString() is { } id) selfCalls.Add(id);
        }

        // A result is matched on its content or error alone, since its telemetry repeats the call's arguments.
        private static void NoteOutput(Finder.Scan scan, string sessionId, JsonElement? record, HashSet<string> selfCalls)
        {
            var data = record.Get("data");
            if (data.Get("toolCallId").AsString() is { } id && selfCalls.Contains(id)) return;
            var result = data.Get("result");
            if ((result ?? data.Get("error")) is not { } output) return;
            var label = result is null ? "tool error" : "tool output";
            scan.Note("copilot", sessionId, output.GetRawText(), Finder.Utc(record.Get("timestamp").StrOr("")), () => OutputExcerpt(output, label, scan.Terms));
        }

        // A result carries no tool name, and a nested "name" would relabel its path, so the label goes in front.
        private static Finder.Snippet? OutputExcerpt(JsonElement output, string label, ImmutableArray<string> terms) =>
            Finder.Excerpt(output, "", terms) is { } excerpt
                ? excerpt with
                {
                    Where = $"{label} {excerpt.Where}".TrimEnd()
                }
                : null;
    }
}
