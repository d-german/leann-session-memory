using System.Text.Json;
using static SessionMemory.ParserHelpers;

namespace SessionMemory.Clients.Claude
{
    // Turns the Claude Code transcripts into one document per session and per subagent run.
    sealed internal partial class ClaudeClient
    {
        private static FamilyResult ReadSessions(string home)
        {
            var documents = new List<Document>();
            var stats = Stats("files", "parent_files", "subagent_files", "malformed", "partial_trailing");
            foreach (var path in Transcripts(home))
            {
                stats["files"]++;
                var subagent = path.Split(Path.DirectorySeparatorChar).Any(part => part.Equals("subagents", StringComparison.OrdinalIgnoreCase));
                stats[subagent ? "subagent_files" : "parent_files"]++;
                var client = subagent ? "claude-code-subagent" : "claude-code";
                var fileStats = new JsonlStats();
                var (sessions, title) = ReadFile(path, subagent, fileStats);
                stats["malformed"] += fileStats.Malformed;
                stats["partial_trailing"] += fileStats.PartialTrailing;
                foreach (var (sessionId, messages) in sessions)
                {
                    documents.Add(new Document(FamilyName, client, sessionId, path, [.. messages.Values.OrderBy(message => message.Sequence)])
                    {
                        Title = title
                    });
                }
            }
            return new FamilyResult([.. documents], stats);
        }

        private static (Dictionary<string, Dictionary<string, Message>> Sessions, string Title) ReadFile(string path, bool subagent, JsonlStats fileStats)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var sessions = new Dictionary<string, Dictionary<string, Message>>();
            string? customTitle = null;
            string? aiTitle = null;
            long sequence = 0;
            foreach (var entry in Jsonl.Read(path, fileStats, "\"type\":\"user\"", "\"type\":\"assistant\"", "\"type\": \"user\"", "\"type\": \"assistant\"",
                         "\"customTitle\"", "\"aiTitle\""))
            {
                var line = (JsonElement?)entry;
                var type = line.Get("type").AsString();
                // A title the user set wins over the generated one; the latest of each wins.
                switch (type)
                {
                    case "custom-title":
                        customTitle = line.Get("customTitle").AsString() ?? customTitle;
                        break;
                    case "ai-title":
                        aiTitle = line.Get("aiTitle").AsString() ?? aiTitle;
                        break;
                }
                if (LineMessage(line, type, sequence) is not { } message) continue;
                var sessionId = SessionId(line, stem, subagent);
                if (!sessions.TryGetValue(sessionId, out var messages)) sessions[sessionId] = messages = [];
                messages[message.Id] = message;
                sequence++;
            }
            return (sessions, customTitle ?? aiTitle ?? "");
        }

        private static Message? LineMessage(JsonElement? line, string? type, long sequence)
        {
            if (type is not ("user" or "assistant") || line.Get("isMeta") is { ValueKind: JsonValueKind.True }) return null;
            var message = line.Get("message");
            var text = (message is { ValueKind: JsonValueKind.Object } ? message.Get("content") : null).ExtractContent("text");
            if (text.Length == 0) return null;
            var messageId = line.Get("uuid").StrOr(message.Get("id").StrOr(Number(sequence)));
            return new Message(messageId, type, text, sequence, line.Get("timestamp").StrOr(""));
        }

        // Each subagent transcript is its own session, even when it shares the parent's session id.
        private static string SessionId(JsonElement? line, string stem, bool subagent)
        {
            var sessionId = line.Get("sessionId").StrOr(stem);
            return subagent ? $"{sessionId}:subagent:{stem}" : sessionId;
        }
    }
}
