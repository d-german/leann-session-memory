using System.Text.Json;
using CSharpFunctionalExtensions;
using static SessionMemory.ParserHelpers;

namespace SessionMemory.Clients.Copilot
{
    // Turns the Copilot event logs and session store into one document per session.
    sealed internal partial class CopilotClient
    {
        private static FamilyResult ReadSessions(string home)
        {
            var storePath = Path.Combine(StoreFolder(home), "session-store.db");
            var desktopIds = DesktopIds(Path.Combine(StoreFolder(home), "data.db"));
            var documents = new List<Document>();
            var eventSessionIds = new HashSet<string>();
            var stats = Stats("event_files", "event_sessions", "store_sessions", "malformed", "partial_trailing", "desktop_sessions");
            foreach (var path in EventFiles(home))
            {
                stats["event_files"]++;
                if (EventDocument(path, desktopIds, stats) is not { } document) continue;
                eventSessionIds.Add(document.SessionId);
                documents.Add(document);
            }
            stats["event_sessions"] = eventSessionIds.Count;
            if (!File.Exists(storePath)) return new FamilyResult([.. documents], stats);
            var storeDocuments = StoreDocuments(storePath, eventSessionIds, desktopIds, stats);
            documents.AddRange(storeDocuments);
            stats["store_sessions"] = storeDocuments.Count;
            return new FamilyResult([.. documents], stats);
        }

        // Desktop sessions are optional: a data.db whose sessions table cannot be read marks none. A file that cannot be opened fails the family.
        private static HashSet<string> DesktopIds(string dataPath)
        {
            if (!File.Exists(dataPath)) return [];
            using var desktop = new SqliteReader(dataPath);
            return [.. desktop.TryQuery("SELECT id FROM sessions").GetValueOrDefault([]).Select(row => Sql.Str(row[0]))];
        }

        private static Document? EventDocument(string path, HashSet<string> desktopIds, Dictionary<string, long> stats)
        {
            var fileStats = new JsonlStats();
            var (sessionId, producer, messages) = EventMessages(path, fileStats);
            stats["malformed"] += fileStats.Malformed;
            stats["partial_trailing"] += fileStats.PartialTrailing;
            if (messages.Count == 0) return null;
            var isDesktop = desktopIds.Contains(sessionId) || producer.Contains("desktop", StringComparison.OrdinalIgnoreCase);
            if (isDesktop) stats["desktop_sessions"]++;
            return new Document(FamilyName, isDesktop ? "github-copilot-desktop" : "github-copilot-cli", sessionId, path, [.. messages])
            {
                Title = WorkspaceName(WorkspaceFile(path))
            };
        }

        private static (string SessionId, string Producer, List<Message> Messages) EventMessages(string path, JsonlStats fileStats)
        {
            var sessionId = Path.GetFileName(Path.GetDirectoryName(path)!);
            var producer = "";
            var users = new Dictionary<string, Message>();
            var assistants = new Dictionary<string, AssistantMessage>();
            long sequence = 0;
            foreach (var entry in Jsonl.Read(path, fileStats, "\"session.start\"", "\"user.message\"", "\"assistant.message\""))
            {
                var line = (JsonElement?)entry;
                var type = line.Get("type").AsString();
                var data = line.Get("data");
                switch (type)
                {
                    case "session.start":
                        sessionId = data.Get("sessionId").StrOr(sessionId);
                        producer = data.Get("producer").StrOr("");
                        break;
                    case "user.message" when data.Get("content").AsString() is { Length: > 0 } userText:
                        var userId = data.Get("messageId").StrOr(data.Get("interactionId").StrOr(line.Get("id").StrOr(Number(sequence))));
                        users[userId] = new Message(userId, "user", userText, sequence, line.Get("timestamp").StrOr(""));
                        sequence++;
                        break;
                    case "assistant.message" when data.Get("phase").PhaseAllowed() && data.Get("content").AsString() is { Length: > 0 } text:
                        AddAssistant(assistants, line, data, text, sequence);
                        sequence++;
                        break;
                }
            }
            var messages = users.Values.ToList();
            foreach (var (messageId, assistant) in assistants)
            {
                var text = assistant.Text;
                if (text.Length > 0) messages.Add(new Message(messageId, "assistant", text, assistant.Sequence, assistant.Timestamp));
            }
            return (sessionId, producer, messages);
        }

        private static void AddAssistant(Dictionary<string, AssistantMessage> assistants, JsonElement? line, JsonElement? data, string text, long sequence)
        {
            var messageId = data.Get("messageId").StrOr(line.Get("id").StrOr(Number(sequence)));
            if (!assistants.TryGetValue(messageId, out var assistant))
                assistants[messageId] = assistant = new AssistantMessage(sequence, line.Get("timestamp").StrOr(""));
            if (ChunkIndex(data.Get("chunkIndex")) is { } chunk) assistant.Chunks[chunk] = text;
            else assistant.Last = text;
        }

        private static List<Document> StoreDocuments(string storePath, HashSet<string> eventSessionIds, HashSet<string> desktopIds, Dictionary<string, long> stats)
        {
            using var store = new SqliteReader(storePath);
            var sessionRows = new Dictionary<string, object?[]>();
            foreach (var row in store.Query("SELECT id, cwd, repository, host_type, summary FROM sessions")) sessionRows[Sql.Str(row[0])] = row;
            var documents = new List<Document>();
            foreach (var (sessionId, messages) in StoreTurns(store, eventSessionIds))
            {
                var session = sessionRows.GetValueOrDefault(sessionId) ?? [null, "", "", "", ""];
                var isDesktop = desktopIds.Contains(sessionId);
                if (isDesktop) stats["desktop_sessions"]++;
                var sourceRef = $"{storePath}#session_id={sessionId};host_type={Sql.StrOr(session[3], "")};cwd={Sql.StrOr(session[1], "")};repository={Sql.StrOr(session[2], "")}";
                documents.Add(new Document(FamilyName, isDesktop ? "github-copilot-desktop" : "github-copilot-cli-store", sessionId, sourceRef, [.. messages])
                {
                    Title = Sql.StrOr(session[4], "")
                });
            }
            return documents;
        }

        private static Dictionary<string, List<Message>> StoreTurns(SqliteReader store, HashSet<string> eventSessionIds)
        {
            var grouped = new Dictionary<string, List<Message>>();
            foreach (var row in store.Query("SELECT id, session_id, turn_index, user_message, assistant_response, timestamp FROM turns ORDER BY session_id, turn_index"))
            {
                var sessionId = Sql.Str(row[1]);
                if (eventSessionIds.Contains(sessionId)) continue;
                var turn = Sql.IntOr(row[2], 0) * 2;
                var timestamp = Sql.StrOr(row[5], "");
                if (row[3] is string { Length: > 0 } userText) Add(grouped, sessionId, new Message($"{Sql.Str(row[0])}:user", "user", userText, turn, timestamp));
                if (row[4] is string { Length: > 0 } assistantText) Add(grouped, sessionId, new Message($"{Sql.Str(row[0])}:assistant", "assistant", assistantText, turn + 1, timestamp));
            }
            return grouped;
        }

        // The session's top-level "name:" from Copilot's workspace.yaml, a flat file that needs no YAML parser; empty when it has none or cannot be read.
        public static string WorkspaceName(string path) =>
            Attempt.Io(() => ReadWorkspaceName(path)).GetValueOrDefault(Maybe<string>.None).GetValueOrDefault("");

        private static Maybe<string> ReadWorkspaceName(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith("name: ", StringComparison.Ordinal)) return Unquote(line[6..].Trim());
            }
            return Maybe<string>.None;
        }

        private static string Unquote(string value) => value switch
        {
            ['\'', .., '\''] => value[1..^1].Replace("''", "'"),
            ['"', .., '"'] => value[1..^1].Replace("\\\"", "\""),
            _ => value
        };

        // Python treats JSON booleans as integers here.
        private static long? ChunkIndex(JsonElement? value) => value?.ValueKind switch
        {
            JsonValueKind.Number when value.Value.TryGetInt64(out var index) => index,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null
        };

        sealed private class AssistantMessage
        {
            public AssistantMessage(long sequence, string timestamp)
            {
                Sequence = sequence;
                Timestamp = timestamp;
            }

            public long Sequence { get; }

            public string Timestamp { get; }

            public SortedDictionary<long, string> Chunks { get; } = [];

            public string Last { get; set; } = "";

            public string Text => Chunks.Count > 0 ? string.Concat(Chunks.Values) : Last;
        }
    }
}
