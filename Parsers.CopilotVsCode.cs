using System.Text.Json;
using System.Text.Json.Nodes;

namespace SessionMemory;

static partial class Parsers
{
    internal static string CodeUser(string home) => Path.Combine(home, "AppData", "Roaming", "Code", "User");

    static FamilyResult Copilot(string home)
    {
        var storePath = Path.Combine(CopilotRoot(home), "session-store.db");
        var dataPath = Path.Combine(CopilotRoot(home), "data.db");
        var desktopIds = new HashSet<string>();
        if (File.Exists(dataPath))
        {
            using var desktop = new SqliteReader(dataPath);
            try
            {
                foreach (var row in desktop.Query("SELECT id FROM sessions")) desktopIds.Add(Sql.Str(row[0]));
            }
            catch (IOException)
            {
                desktopIds.Clear();
            }
        }
        var documents = new List<Document>();
        var eventSessionIds = new HashSet<string>();
        var stats = Stats("event_files", "event_sessions", "store_sessions", "malformed", "partial_trailing", "desktop_sessions");
        foreach (var path in CopilotEventFiles(home))
        {
            stats["event_files"]++;
            var fileStats = new JsonlStats();
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
                if (type == "session.start")
                {
                    sessionId = data.Get("sessionId").StrOr(sessionId);
                    producer = data.Get("producer").StrOr("");
                    continue;
                }
                if (type == "user.message")
                {
                    if (data.Get("content").AsString() is not { Length: > 0 } userText) continue;
                    var userId = data.Get("messageId").StrOr(data.Get("interactionId").StrOr(line.Get("id").StrOr(Number(sequence))));
                    users[userId] = new Message(userId, "user", userText, sequence, line.Get("timestamp").StrOr(""));
                    sequence++;
                    continue;
                }
                if (type != "assistant.message" || !data.Get("phase").PhaseAllowed()) continue;
                if (data.Get("content").AsString() is not { Length: > 0 } text) continue;
                var messageId = data.Get("messageId").StrOr(line.Get("id").StrOr(Number(sequence)));
                if (!assistants.TryGetValue(messageId, out var assistant))
                    assistants[messageId] = assistant = new AssistantMessage(sequence, line.Get("timestamp").StrOr(""));
                if (ChunkIndex(data.Get("chunkIndex")) is { } chunk) assistant.Chunks[chunk] = text;
                else assistant.Last = text;
                sequence++;
            }
            stats["malformed"] += fileStats.Malformed;
            stats["partial_trailing"] += fileStats.PartialTrailing;
            var messages = users.Values.ToList();
            foreach (var (messageId, assistant) in assistants)
            {
                var text = assistant.Chunks.Count > 0 ? string.Concat(assistant.Chunks.Values) : assistant.Last;
                if (text.Length > 0) messages.Add(new Message(messageId, "assistant", text, assistant.Sequence, assistant.Timestamp));
            }
            if (messages.Count == 0) continue;
            eventSessionIds.Add(sessionId);
            var isDesktop = desktopIds.Contains(sessionId) || producer.Contains("desktop", StringComparison.OrdinalIgnoreCase);
            if (isDesktop) stats["desktop_sessions"]++;
            documents.Add(new Document("copilot", isDesktop ? "github-copilot-desktop" : "github-copilot-cli", sessionId, path, messages)
            {
                Title = WorkspaceName(WorkspaceFile(path)),
            });
        }
        stats["event_sessions"] = eventSessionIds.Count;
        if (!File.Exists(storePath)) return new FamilyResult(documents, stats);
        using var store = new SqliteReader(storePath);
        var sessionRows = new Dictionary<string, object?[]>();
        foreach (var row in store.Query("SELECT id, cwd, repository, host_type, summary FROM sessions")) sessionRows[Sql.Str(row[0])] = row;
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
        foreach (var (sessionId, messages) in grouped)
        {
            var session = sessionRows.GetValueOrDefault(sessionId) ?? [null, "", "", "", ""];
            var isDesktop = desktopIds.Contains(sessionId);
            if (isDesktop) stats["desktop_sessions"]++;
            var sourceRef = $"{storePath}#session_id={sessionId};host_type={Sql.StrOr(session[3], "")};cwd={Sql.StrOr(session[1], "")};repository={Sql.StrOr(session[2], "")}";
            documents.Add(new Document("copilot", isDesktop ? "github-copilot-desktop" : "github-copilot-cli-store", sessionId, sourceRef, messages)
            {
                Title = Sql.StrOr(session[4], ""),
            });
        }
        stats["store_sessions"] = grouped.Count;
        return new FamilyResult(documents, stats);
    }

    // The session's top-level "name:" from Copilot's workspace.yaml, a flat file that needs no YAML parser.
    public static string WorkspaceName(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!line.StartsWith("name: ", StringComparison.Ordinal)) continue;
                var value = line[6..].Trim();
                if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1].Replace("''", "'");
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') return value[1..^1].Replace("\\\"", "\"");
                return value;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return "";
    }

    // Python treats JSON booleans as integers here.
    static long? ChunkIndex(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.Number when value.Value.TryGetInt64(out var index) => index,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => null,
    };

    internal static IReadOnlyList<string> ChatFiles(string codeUser)
    {
        string[][] patterns =
        [
            ["workspaceStorage", "*", "chatSessions"],
            ["profiles", "*", "workspaceStorage", "*", "chatSessions"],
            ["globalStorage", "*", "chatSessions"],
            ["profiles", "*", "globalStorage", "*", "chatSessions"],
            ["globalStorage", "emptyWindowChatSessions"],
            ["profiles", "*", "globalStorage", "emptyWindowChatSessions"],
        ];
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            List<string> directories = Directory.Exists(codeUser) ? [codeUser] : [];
            foreach (var part in pattern)
            {
                directories = [.. directories.SelectMany(SubDirectories)
                    .Where(directory => part == "*" || Path.GetFileName(directory).Equals(part, StringComparison.OrdinalIgnoreCase))];
            }
            foreach (var directory in directories) found.UnionWith(Files(directory, "*.jsonl"));
        }
        var files = found.ToList();
        files.Sort(ComparePaths);
        return files;
    }

    // Python sorts Windows paths by their lower-cased components.
    static int ComparePaths(string left, string right)
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

    static FamilyResult VsCode(string home)
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
            var messages = new List<Message>();
            if (state["requests"] is JsonArray requests)
            {
                for (var index = 0; index < requests.Count; index++)
                {
                    if (requests[index] is not JsonObject request || request["hiddenFromTranscript"].Kind() == JsonValueKind.True) continue;
                    var timestamp = request["timestamp"].IsoFromMs();
                    var text = UserText(request["message"]);
                    if (text.Length > 0) messages.Add(new Message(request["requestId"].StrOr($"{index}:user"), "user", text, index * 2, timestamp));
                    text = AssistantText(request["response"]);
                    if (text.Length == 0) continue;
                    var responseTimestamp = request["responseTimestamp"].IsoFromMs();
                    messages.Add(new Message(request["responseId"].StrOr($"{index}:assistant"), "assistant", text, index * 2 + 1,
                        responseTimestamp.Length > 0 ? responseTimestamp : timestamp));
                }
            }
            if (messages.Count > 0) documents.Add(new Document("vscode", "vscode-copilot", sessionId, path, messages) { Title = state["customTitle"].AsString() ?? "" });
        }
        stats["sessions"] = documents.Count;
        return new FamilyResult(documents, stats);
    }

    static string UserText(JsonNode? message)
    {
        if (message is not JsonObject item) return "";
        if (item["text"].AsString() is { Length: > 0 } text) return text;
        if (item["parts"] is not JsonArray parts) return "";
        return string.Join('\n', parts.OfType<JsonObject>().Select(part => part["text"].AsString() ?? part["value"].AsString()).OfType<string>());
    }

    static string AssistantText(JsonNode? response)
    {
        if (response is not JsonArray items) return "";
        return string.Join('\n', items.OfType<JsonObject>()
            .Where(item => item["kind"] is null)
            .Select(item => item["value"].AsString() ?? (item["value"] as JsonObject)?["value"].AsString())
            .OfType<string>());
    }

    // VS Code chat files are edit logs (ObjectMutationLog): kind 0 sets the state, 1 sets a path, 2 cuts an array
    // at "i" and appends "v", 3 deletes a property.
    public static (JsonObject State, long Errors) Replay(string path, JsonlStats stats)
    {
        var state = new JsonObject();
        long errors = 0;
        foreach (var operation in Jsonl.ReadNodes(path, stats))
        {
            try
            {
                var kind = OperationKind(operation["kind"]);
                if (kind == 0 && operation["v"] is JsonObject initial)
                {
                    operation.Remove("v");
                    state = initial;
                }
                else if (kind == 1 && state.Count > 0)
                {
                    SetPath(state, Keys(operation), Take(operation));
                }
                else if (kind == 2 && state.Count > 0)
                {
                    Push(state, Keys(operation), operation["i"], Take(operation));
                }
                else if (kind == 3 && state.Count > 0)
                {
                    SetPath(state, Keys(operation), null);
                }
            }
            catch (ReplayException)
            {
                errors++;
            }
        }
        return (state, errors);
    }

    static int OperationKind(JsonNode? kind) => kind.Kind() switch
    {
        JsonValueKind.Number => kind!.AsValue().TryGetValue<double>(out var number) && number is 0 or 1 or 2 or 3 ? (int)number : -1,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => -1,
    };

    static List<JsonNode?> Keys(JsonObject operation) =>
        operation.TryGetPropertyValue("k", out var keys) && keys is JsonArray path ? [.. path] : throw new ReplayException();

    static JsonNode? Take(JsonObject operation)
    {
        if (!operation.TryGetPropertyValue("v", out var value)) return null;
        operation.Remove("v");
        return value;
    }

    static void SetPath(JsonObject root, List<JsonNode?> keys, JsonNode? value)
    {
        if (keys.Count == 0) throw new ReplayException();
        JsonNode? parent = root;
        for (var index = 0; index < keys.Count - 1; index++) parent = Child(parent, keys[index]);
        var last = keys[^1];
        if (parent is JsonObject item && last.AsString() is { } name)
        {
            item[name] = value;
        }
        else if (parent is JsonArray list && Index(last) is { } position)
        {
            var target = position < 0 ? position + list.Count : position;
            if (target < 0 || target >= list.Count) throw new ReplayException();
            list[(int)target] = value;
        }
        else if (parent is not JsonObject || Index(last) is null)
        {
            // Python accepts a numeric key on an object (a no-op here, transcripts never read it); anything else fails.
            throw new ReplayException();
        }
    }

    // "i" rewrites the array from that index: a streamed or edited item replaces the rest, and "i" alone removes them.
    static void Push(JsonObject root, List<JsonNode?> keys, JsonNode? index, JsonNode? value)
    {
        JsonNode? target = root;
        foreach (var key in keys) target = Child(target, key);
        if (target is not JsonArray items) throw new ReplayException();
        if (index is not null)
        {
            var cut = Index(index) is { } at and >= 0 ? at : throw new ReplayException();
            while (items.Count > cut) items.RemoveAt(items.Count - 1);
        }
        if (value is not JsonArray additions) return;
        List<JsonNode?> moved = [.. additions];
        additions.Clear();
        foreach (var addition in moved) items.Add(addition);
    }

    static JsonNode? Child(JsonNode? node, JsonNode? key)
    {
        if (node is JsonObject item && key.AsString() is { } name)
            return item.TryGetPropertyValue(name, out var child) ? child : throw new ReplayException();
        if (node is JsonArray list && Index(key) is { } position)
        {
            var target = position < 0 ? position + list.Count : position;
            return target >= 0 && target < list.Count ? list[(int)target] : throw new ReplayException();
        }
        throw new ReplayException();
    }

    static long? Index(JsonNode? key) => key.Kind() switch
    {
        JsonValueKind.Number => key!.AsValue().TryGetValue<long>(out var number) ? number : null,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => null,
    };

    sealed class AssistantMessage
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
    }

    sealed class ReplayException : Exception
    {
    }
}
