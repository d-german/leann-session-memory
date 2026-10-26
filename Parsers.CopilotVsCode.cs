using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpFunctionalExtensions;

namespace SessionMemory
{
    internal static partial class Parsers
    {
        internal static string CodeUser(string home) => Path.Combine(home, "AppData", "Roaming", "Code", "User");

        private static FamilyResult Copilot(string home)
        {
            var storePath = Path.Combine(CopilotRoot(home), "session-store.db");
            var desktopIds = CopilotDesktopIds(Path.Combine(CopilotRoot(home), "data.db"));
            var documents = new List<Document>();
            var eventSessionIds = new HashSet<string>();
            var stats = Stats("event_files", "event_sessions", "store_sessions", "malformed", "partial_trailing", "desktop_sessions");
            foreach (var path in CopilotEventFiles(home))
            {
                stats["event_files"]++;
                if (CopilotEventDocument(path, desktopIds, stats) is not { } document) continue;
                eventSessionIds.Add(document.SessionId);
                documents.Add(document);
            }
            stats["event_sessions"] = eventSessionIds.Count;
            if (!File.Exists(storePath)) return new FamilyResult([.. documents], stats);
            var storeDocuments = CopilotStoreDocuments(storePath, eventSessionIds, desktopIds, stats);
            documents.AddRange(storeDocuments);
            stats["store_sessions"] = storeDocuments.Count;
            return new FamilyResult([.. documents], stats);
        }

        // Desktop sessions are optional: a data.db whose sessions table cannot be read marks none. A file that cannot be opened fails the family.
        private static HashSet<string> CopilotDesktopIds(string dataPath)
        {
            if (!File.Exists(dataPath)) return [];
            using var desktop = new SqliteReader(dataPath);
            return [.. desktop.TryQuery("SELECT id FROM sessions").GetValueOrDefault([]).Select(row => Sql.Str(row[0]))];
        }

        private static Document? CopilotEventDocument(string path, HashSet<string> desktopIds, Dictionary<string, long> stats)
        {
            var fileStats = new JsonlStats();
            var (sessionId, producer, messages) = CopilotEventMessages(path, fileStats);
            stats["malformed"] += fileStats.Malformed;
            stats["partial_trailing"] += fileStats.PartialTrailing;
            if (messages.Count == 0) return null;
            var isDesktop = desktopIds.Contains(sessionId) || producer.Contains("desktop", StringComparison.OrdinalIgnoreCase);
            if (isDesktop) stats["desktop_sessions"]++;
            return new Document("copilot", isDesktop ? "github-copilot-desktop" : "github-copilot-cli", sessionId, path, [.. messages])
            {
                Title = WorkspaceName(WorkspaceFile(path))
            };
        }

        private static (string SessionId, string Producer, List<Message> Messages) CopilotEventMessages(string path, JsonlStats fileStats)
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
                        CopilotAddAssistant(assistants, line, data, text, sequence);
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

        private static void CopilotAddAssistant(Dictionary<string, AssistantMessage> assistants, JsonElement? line, JsonElement? data, string text, long sequence)
        {
            var messageId = data.Get("messageId").StrOr(line.Get("id").StrOr(Number(sequence)));
            if (!assistants.TryGetValue(messageId, out var assistant))
                assistants[messageId] = assistant = new AssistantMessage(sequence, line.Get("timestamp").StrOr(""));
            if (ChunkIndex(data.Get("chunkIndex")) is { } chunk) assistant.Chunks[chunk] = text;
            else assistant.Last = text;
        }

        private static List<Document> CopilotStoreDocuments(string storePath, HashSet<string> eventSessionIds, HashSet<string> desktopIds, Dictionary<string, long> stats)
        {
            using var store = new SqliteReader(storePath);
            var sessionRows = new Dictionary<string, object?[]>();
            foreach (var row in store.Query("SELECT id, cwd, repository, host_type, summary FROM sessions")) sessionRows[Sql.Str(row[0])] = row;
            var documents = new List<Document>();
            foreach (var (sessionId, messages) in CopilotStoreTurns(store, eventSessionIds))
            {
                var session = sessionRows.GetValueOrDefault(sessionId) ?? [null, "", "", "", ""];
                var isDesktop = desktopIds.Contains(sessionId);
                if (isDesktop) stats["desktop_sessions"]++;
                var sourceRef = $"{storePath}#session_id={sessionId};host_type={Sql.StrOr(session[3], "")};cwd={Sql.StrOr(session[1], "")};repository={Sql.StrOr(session[2], "")}";
                documents.Add(new Document("copilot", isDesktop ? "github-copilot-desktop" : "github-copilot-cli-store", sessionId, sourceRef, [.. messages])
                {
                    Title = Sql.StrOr(session[4], "")
                });
            }
            return documents;
        }

        private static Dictionary<string, List<Message>> CopilotStoreTurns(SqliteReader store, HashSet<string> eventSessionIds)
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
                if (line.StartsWith("name: ", StringComparison.Ordinal)) return WorkspaceUnquote(line[6..].Trim());
            }
            return Maybe<string>.None;
        }

        private static string WorkspaceUnquote(string value) => value switch
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

        internal static ImmutableArray<string> ChatFiles(string codeUser)
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

        private static FamilyResult VsCode(string home)
        {
            var documents = new List<Document>();
            var stats = Stats("files", "sessions", "operations_failed", "malformed", "partial_trailing");
            foreach (var path in ChatFiles(CodeUser(home)))
            {
                stats["files"]++;
                var fileStats = new JsonlStats();
                var (state, errors) = Replay(path, fileStats);
                stats["operations_failed"] += errors;
                stats["malformed"] += fileStats.Malformed;
                stats["partial_trailing"] += fileStats.PartialTrailing;
                var sessionId = state["sessionId"].StrOr(Path.GetFileNameWithoutExtension(path));
                var messages = VsCodeMessages(state["requests"]);
                if (messages.Count > 0)
                    documents.Add(new Document("vscode", "vscode-copilot", sessionId, path, [.. messages])
                    {
                        Title = state["customTitle"].AsString() ?? ""
                    });
            }
            stats["sessions"] = documents.Count;
            return new FamilyResult([.. documents], stats);
        }

        private static List<Message> VsCodeMessages(JsonNode? requests)
        {
            var messages = new List<Message>();
            if (requests is not JsonArray items) return messages;
            for (var index = 0; index < items.Count; index++)
            {
                if (items[index] is not JsonObject request || request["hiddenFromTranscript"].Kind() == JsonValueKind.True) continue;
                VsCodeAddRequest(messages, request, index);
            }
            return messages;
        }

        private static void VsCodeAddRequest(List<Message> messages, JsonObject request, int index)
        {
            var timestamp = request["timestamp"].IsoFromMs();
            var text = UserText(request["message"]);
            if (text.Length > 0) messages.Add(new Message(request["requestId"].StrOr($"{index}:user"), "user", text, index * 2, timestamp));
            text = AssistantText(request["response"]);
            if (text.Length == 0) return;
            var responseTimestamp = request["responseTimestamp"].IsoFromMs();
            messages.Add(new Message(request["responseId"].StrOr($"{index}:assistant"), "assistant", text, index * 2 + 1,
                responseTimestamp.Length > 0 ? responseTimestamp : timestamp));
        }

        private static string UserText(JsonNode? message)
        {
            if (message is not JsonObject item) return "";
            if (item["text"].AsString() is { Length: > 0 } text) return text;
            return item["parts"] is JsonArray parts
                ? string.Join('\n', parts.OfType<JsonObject>().Select(part => part["text"].AsString() ?? part["value"].AsString()).OfType<string>())
                : "";
        }

        private static string AssistantText(JsonNode? response)
        {
            if (response is not JsonArray items) return "";
            return string.Join('\n', items.OfType<JsonObject>()
                .Where(item => item["kind"] is null)
                .Select(item => item["value"].AsString() ?? (item["value"] as JsonObject)?["value"].AsString())
                .OfType<string>());
        }

        // VS Code chat files are edit logs (ObjectMutationLog): kind 0 sets the state, 1 sets a path, 2 cuts an array
        // at "i" and appends "v", 3 deletes a property. An operation that does not fit the state changes nothing and is counted.
        public static (JsonObject State, long Errors) Replay(string path, JsonlStats stats)
        {
            var state = new JsonObject();
            long errors = 0;
            foreach (var operation in Jsonl.ReadNodes(path, stats))
            {
                if (Apply(state, operation).TryGetValue(out var next)) state = next;
                else errors++;
            }
            return (state, errors);
        }

        // Edits before the first state, unknown kinds and an initial state that is not an object are skipped and keep the state.
        private static Result<JsonObject, ReplayError> Apply(JsonObject state, JsonObject operation) => OperationKind(operation["kind"]) switch
        {
            0 when operation["v"] is JsonObject initial => Adopt(operation, initial),
            1 when state.Count > 0 => Keys(operation).Bind(keys => SetPath(state, keys, Take(operation))).Map(() => state),
            2 when state.Count > 0 => Keys(operation).Bind(keys => Push(state, keys, operation["i"], Take(operation))).Map(() => state),
            3 when state.Count > 0 => Keys(operation).Bind(keys => SetPath(state, keys, null)).Map(() => state),
            _ => state
        };

        // The initial state leaves its operation, which is discarded, so it can be edited as the root.
        private static JsonObject Adopt(JsonObject operation, JsonObject initial)
        {
            operation.Remove("v");
            return initial;
        }

        private static int OperationKind(JsonNode? kind) => kind.Kind() switch
        {
            JsonValueKind.Number => kind!.AsValue().TryGetValue<double>(out var number) && number is 0 or 1 or 2 or 3 ? (int)number : -1,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => -1
        };

        // A path needs at least one key: with none there is no property to set and the root is never an array.
        private static Result<JsonArray, ReplayError> Keys(JsonObject operation) =>
            operation["k"] is JsonArray { Count: > 0 } keys ? keys : ReplayError.BadPath;

        private static JsonNode? Take(JsonObject operation)
        {
            if (!operation.TryGetPropertyValue("v", out var value)) return null;
            operation.Remove("v");
            return value;
        }

        // Every key but the last must lead to an existing container; the last names the property or position to set.
        private static UnitResult<ReplayError> SetPath(JsonObject root, JsonArray keys, JsonNode? value) =>
            Walk(root, keys.SkipLast(1)).Bind(parent => Assign(parent, keys[^1], value));

        private static UnitResult<ReplayError> Assign(JsonNode parent, JsonNode? last, JsonNode? value)
        {
            switch (parent)
            {
                case JsonObject item when last.AsString() is { } name:
                    item[name] = value;
                    return UnitResult.Success<ReplayError>();
                case JsonArray list when Index(last) is { } position:
                    return ReplayPosition(list, position).Tap(at => list[at] = value);
                // Python accepts a numeric key on an object (a no-op here, transcripts never read it); anything else fails.
                case JsonObject when Index(last) is not null:
                    return UnitResult.Success<ReplayError>();
                default:
                    return ReplayError.BadKeyType;
            }
        }

        // "i" rewrites the array from that index: a streamed or edited item replaces the rest, and "i" alone removes them.
        private static UnitResult<ReplayError> Push(JsonObject root, JsonArray keys, JsonNode? index, JsonNode? value) =>
            Walk(root, keys)
                .Bind(target => (target as JsonArray).ToResult(ReplayError.NotArray))
                .Bind(items => Cut(index).Tap(cut => Splice(items, cut, value)));

        // Without "i" nothing is cut; a cut past the end keeps every item.
        private static Result<long, ReplayError> Cut(JsonNode? index) => index switch
        {
            null => long.MaxValue,
            _ when Index(index) is { } at and >= 0 => at,
            _ => ReplayError.BadIndex
        };

        private static void Splice(JsonArray items, long cut, JsonNode? value)
        {
            while (items.Count > cut) items.RemoveAt(items.Count - 1);
            if (value is not JsonArray additions) return;
            List<JsonNode?> moved = [.. additions];
            additions.Clear();
            foreach (var addition in moved) items.Add(addition);
        }

        private static Result<JsonNode, ReplayError> Walk(JsonNode root, IEnumerable<JsonNode?> keys) =>
            keys.Aggregate(Result.Success<JsonNode, ReplayError>(root), (node, key) => node.Bind(parent => Child(parent, key)));

        // A key must name an existing property of an object or a position in an array; a null value cannot be walked into.
        private static Result<JsonNode, ReplayError> Child(JsonNode node, JsonNode? key) => node switch
        {
            JsonObject item when key.AsString() is { } name => item[name].ToResult(ReplayError.MissingKey),
            JsonArray list when Index(key) is { } position => ReplayPosition(list, position).Bind(at => list[at].ToResult(ReplayError.MissingKey)),
            _ => ReplayError.BadKeyType
        };

        // A negative position counts from the end, as in Python.
        private static Result<int, ReplayError> ReplayPosition(JsonArray list, long position)
        {
            var target = position < 0 ? position + list.Count : position;
            return target >= 0 && target < list.Count ? (int)target : ReplayError.OutOfRange;
        }

        private static long? Index(JsonNode? key) => key.Kind() switch
        {
            JsonValueKind.Number => key!.AsValue().TryGetValue<long>(out var number) ? number : null,
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

        // Why an operation did not fit the state; replay only counts them.
        private enum ReplayError
        {
            BadPath,
            MissingKey,
            BadKeyType,
            OutOfRange,
            NotArray,
            BadIndex
        }
    }
}
