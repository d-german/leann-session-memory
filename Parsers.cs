using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SessionMemory;

sealed class Family
{
    public Family(string name, Func<IReadOnlyList<string>> sources, Func<FamilyResult> parse)
    {
        Name = name;
        Sources = sources;
        Parse = parse;
    }

    public string Name { get; }

    // Files whose size and timestamp decide whether the family must be parsed again.
    public Func<IReadOnlyList<string>> Sources { get; }

    public Func<FamilyResult> Parse { get; }
}

/// <summary>Reads each assistant's local transcript store, mirroring the original Python bridge.</summary>
static partial class Parsers
{
    public static IReadOnlyList<Family> All(string home, string root) =>
    [
        Synthetic(root),
        new Family("codex", () => CodexSources(home), () => Codex(home)),
        new Family("claude", () => [.. Rglob(ClaudeRoot(home), "*.jsonl")], () => Claude(home)),
        new Family("copilot", () => CopilotSources(home), () => Copilot(home)),
        new Family("vscode", () => ChatFiles(CodeUser(home)), () => VsCode(home)),
    ];

    public static Family Synthetic(string root) => new("synthetic", () => SyntheticFiles(root), () => ParseSynthetic(root));

    // Every index build is validated by finding this conversation, so a new machine needs it before its first build.
    public static void EnsureCanary(string root)
    {
        var path = Path.Combine(root, "synthetic", "canary.jsonl");
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string[][] messages =
        [
            ["u1", "user", "What retry limit did this conversation decide?"],
            ["a1", "assistant", "The retry limit is 2."],
            ["a2", "assistant", "Correction: the current retry limit is 5 and supersedes 2."],
        ];
        File.WriteAllLines(path, messages.Select(message => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["session_id"] = Indexer.CanaryId, ["message_id"] = message[0], ["role"] = message[1], ["text"] = message[2],
        })));
    }

    internal static string CodexHistory(string home) => Path.Combine(home, ".codex", "thread_history_1.sqlite");

    static string ClaudeRoot(string home) => Path.Combine(home, ".claude", "projects");

    static string CopilotRoot(string home) => Path.Combine(home, ".copilot");

    // Codex compresses older rollouts to .jsonl.zst; some sessions survive only in that form.
    static IEnumerable<string> CodexRollouts(string home) =>
        new[] { "sessions", "archived_sessions" }
            .SelectMany(folder => new[] { "*.jsonl", "*.jsonl.zst" }.SelectMany(pattern => Rglob(Path.Combine(home, ".codex", folder), pattern)));

    static readonly Regex RolloutId = MyRegex();

    // Rollouts also record context Codex injects at the start of user turns, which its SQLite store leaves out.
    // Only these tags are injected; others, such as question replies, review results and voice requests, are the user's.
    static readonly Regex LeadingContext = new(
        @"^\s*(?:<(environment_context|recommended_plugins|turn_aborted|external_codex_apps_open_page)>.*?</\1>" +
        @"|# AGENTS\.md instructions[^\n]*\s*<INSTRUCTIONS>.*?</INSTRUCTIONS>)\s*",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public static string StripInjectedContext(string text)
    {
        string previous;
        do
        {
            previous = text;
            text = LeadingContext.Replace(text, "", 1);
        }
        while (text != previous);
        return text;
    }

    static IReadOnlyList<string> CodexSources(string home) =>
        [.. DatabaseFiles(CodexHistory(home)), .. CodexRollouts(home), CodexIndex(home), .. CodexStates(home).SelectMany(DatabaseFiles)];

    static string CodexIndex(string home) => Path.Combine(home, ".codex", "session_index.jsonl");

    // The version suffix (state_5) changes with Codex releases; the newest file is current.
    static IEnumerable<string> CodexStates(string home) =>
        Directory.Exists(Path.Combine(home, ".codex"))
            ? Directory.EnumerateFiles(Path.Combine(home, ".codex"), "state_*.sqlite").Order(StringComparer.Ordinal).TakeLast(1)
            : [];

    static IReadOnlyList<string> CopilotSources(string home) =>
    [
        .. DatabaseFiles(Path.Combine(CopilotRoot(home), "data.db")),
        .. DatabaseFiles(Path.Combine(CopilotRoot(home), "session-store.db")),
        .. CopilotEventFiles(home),
        .. CopilotEventFiles(home).Select(WorkspaceFile).Where(File.Exists),
    ];

    static string WorkspaceFile(string eventFile) => Path.Combine(Path.GetDirectoryName(eventFile)!, "workspace.yaml");

    static string[] DatabaseFiles(string path) => [path, path + "-wal"];

    internal static IEnumerable<string> CopilotEventFiles(string home)
    {
        var root = Path.Combine(CopilotRoot(home), "session-state");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root).Select(directory => Path.Combine(directory, "events.jsonl")).Where(File.Exists);
    }

    static IReadOnlyList<string> SyntheticFiles(string root)
    {
        var directory = Path.Combine(root, "synthetic");
        return Directory.Exists(directory) ? [.. Directory.EnumerateFiles(directory, "*.jsonl")] : [];
    }

    static FamilyResult Codex(string home)
    {
        var history = CodexHistory(home);
        var sessions = new Dictionary<string, List<Message>>();
        var stats = Stats("sqlite_threads", "fallback_threads", "projection_without_items", "partial_trailing", "malformed");
        if (File.Exists(history))
        {
            using var database = new SqliteReader(history);
            database.Execute("BEGIN");
            const string items = "SELECT thread_id, turn_id, item_id, rollout_ordinal, created_at_ms, item_type, item_json FROM thread_items " +
                "WHERE item_type IN ('userMessage','agentMessage') ORDER BY thread_id, rollout_ordinal, item_id";
            foreach (var row in database.Query(items))
            {
                using var json = Jsonl.Parse(row[6] as string);
                if (json is null)
                {
                    stats["malformed"]++;
                    continue;
                }
                var item = (JsonElement?)json.RootElement;
                string text;
                string role;
                if (row[5] as string == "userMessage")
                {
                    text = item.Get("content").ExtractContent("text");
                    role = "user";
                }
                else
                {
                    if (!item.Get("phase").PhaseAllowed()) continue;
                    text = item.Get("text").AsString() ?? "";
                    role = "assistant";
                }
                if (text.Length > 0)
                    Add(sessions, Sql.Str(row[0]), new Message(Sql.StrOr(row[2], Sql.Str(row[1])), role, text, Sql.IntOr(row[3], 0), Transcript.IsoFromMs(Sql.Number(row[4]))));
            }
            stats["sqlite_threads"] = sessions.Count;
            stats["projection_without_items"] = Sql.IntOr(database.Query(
                "SELECT count(*) FROM thread_history_projection_state p WHERE NOT EXISTS (SELECT 1 FROM thread_items i WHERE i.thread_id=p.thread_id)").First()[0], 0);
            try
            {
                const string realtime = "SELECT thread_id, item_id, rollout_ordinal, created_at_ms, item_json FROM thread_realtime_items " +
                    "WHERE item_type='transcript_segment' ORDER BY thread_id, rollout_ordinal";
                foreach (var row in database.Query(realtime))
                {
                    using var json = Jsonl.Parse(row[4] as string);
                    if (json is null)
                    {
                        stats["malformed"]++;
                        continue;
                    }
                    var item = (JsonElement?)json.RootElement;
                    if (item.Get("role").AsString() is "user" or "assistant" && item.Get("text").AsString() is { Length: > 0 } text)
                        Add(sessions, Sql.Str(row[0]), new Message(Sql.Str(row[1]), item.Get("role").AsString()!, text, Sql.IntOr(row[2], 0), Transcript.IsoFromMs(Sql.Number(row[3]))));
                }
            }
            catch (IOException)
            {
                // Older Codex stores have no realtime table.
            }
            database.Execute("ROLLBACK");
        }
        foreach (var path in CodexRollouts(home))
        {
            // Rollouts are only a fallback, so skip decompressing one whose session SQLite already holds.
            if (path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) && RolloutId.Match(Path.GetFileName(path)) is { Success: true } id
                && sessions.ContainsKey(id.Value)) continue;
            var fileStats = new JsonlStats();
            var sessionId = "";
            var messages = new Dictionary<string, Message>();
            foreach (var entry in Jsonl.Read(path, fileStats, "\"session_meta\"", "\"response_item\""))
            {
                var line = (JsonElement?)entry;
                var payload = line.Get("payload");
                var type = line.Get("type").AsString();
                if (type == "session_meta")
                {
                    sessionId = payload.Get("id").StrOr(payload.Get("session_id").StrOr(sessionId));
                    continue;
                }
                if (type != "response_item" || payload.Get("type").AsString() != "message") continue;
                var role = payload.Get("role").AsString();
                if (role is not ("user" or "assistant")) continue;
                if (role == "assistant" && !payload.Get("phase").PhaseAllowed()) continue;
                var text = payload.Get("content").ExtractContent(role == "user" ? "input_text" : "output_text");
                if (role == "user") text = StripInjectedContext(text);
                if (text.Length == 0) continue;
                var count = messages.Count;
                var messageId = payload.Get("id").StrOr(line.Get("ordinal").StrOr(Number(count)));
                messages[messageId] = new Message(messageId, role, text, line.Get("ordinal").IntOr(count), line.Get("timestamp").StrOr(""));
            }
            stats["partial_trailing"] += fileStats.PartialTrailing;
            stats["malformed"] += fileStats.Malformed;
            if (sessionId.Length == 0) sessionId = Path.GetFileNameWithoutExtension(path);
            if (sessions.ContainsKey(sessionId) || messages.Count == 0) continue;
            sessions[sessionId] = [.. messages.Values];
            stats["fallback_threads"]++;
        }
        var titles = CodexTitles(home);
        var documents = sessions
            .Select(pair => new Document("codex", "codex", pair.Key, $"{history}#thread_id={pair.Key}", pair.Value) { Title = titles.GetValueOrDefault(pair.Key, "") })
            .ToList();
        return new FamilyResult(documents, stats);
    }

    // Thread names come from the session index and the state database; titles are optional, so a schema change only loses them.
    static Dictionary<string, string> CodexTitles(string home)
    {
        var titles = new Dictionary<string, string>();
        if (File.Exists(CodexIndex(home)))
        {
            foreach (var entry in Jsonl.Read(CodexIndex(home), new JsonlStats(), "\"thread_name\""))
            {
                var line = (JsonElement?)entry;
                if (line.Get("id").AsString() is { } id && line.Get("thread_name").AsString() is { Length: > 0 } name) titles[id] = name;
            }
        }
        foreach (var state in CodexStates(home))
        {
            try
            {
                using var database = new SqliteReader(state);
                foreach (var row in database.Query("SELECT id, name FROM threads WHERE name IS NOT NULL AND name <> ''"))
                    titles[Sql.Str(row[0])] = Sql.Str(row[1]);
            }
            catch (IOException)
            {
            }
        }
        return titles;
    }

    static FamilyResult Claude(string home)
    {
        var documents = new List<Document>();
        var stats = Stats("files", "parent_files", "subagent_files", "malformed", "partial_trailing");
        foreach (var path in Rglob(ClaudeRoot(home), "*.jsonl"))
        {
            stats["files"]++;
            var subagent = path.Split(Path.DirectorySeparatorChar).Any(part => part.Equals("subagents", StringComparison.OrdinalIgnoreCase));
            stats[subagent ? "subagent_files" : "parent_files"]++;
            var stem = Path.GetFileNameWithoutExtension(path);
            var fileStats = new JsonlStats();
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
                if (type == "custom-title") customTitle = line.Get("customTitle").AsString() ?? customTitle;
                if (type == "ai-title") aiTitle = line.Get("aiTitle").AsString() ?? aiTitle;
                if (type is not ("user" or "assistant") || line.Get("isMeta") is { ValueKind: JsonValueKind.True }) continue;
                var message = line.Get("message");
                var text = (message is { ValueKind: JsonValueKind.Object } ? message.Get("content") : null).ExtractContent("text");
                if (text.Length == 0) continue;
                var sessionId = line.Get("sessionId").StrOr(stem);
                if (subagent) sessionId = $"{sessionId}:subagent:{stem}";
                var messageId = line.Get("uuid").StrOr(message.Get("id").StrOr(Number(sequence)));
                if (!sessions.TryGetValue(sessionId, out var messages)) sessions[sessionId] = messages = [];
                messages[messageId] = new Message(messageId, type, text, sequence, line.Get("timestamp").StrOr(""));
                sequence++;
            }
            stats["malformed"] += fileStats.Malformed;
            stats["partial_trailing"] += fileStats.PartialTrailing;
            foreach (var (sessionId, messages) in sessions)
            {
                var client = subagent ? "claude-code-subagent" : "claude-code";
                documents.Add(new Document("claude", client, sessionId, path, [.. messages.Values.OrderBy(message => message.Sequence)])
                {
                    Title = customTitle ?? aiTitle ?? "",
                });
            }
        }
        return new FamilyResult(documents, stats);
    }

    static FamilyResult ParseSynthetic(string root)
    {
        var grouped = new Dictionary<string, List<Message>>();
        var sources = new Dictionary<string, string>();
        var stats = Stats("files", "sessions", "malformed", "partial_trailing");
        foreach (var path in SyntheticFiles(root))
        {
            stats["files"]++;
            var fileStats = new JsonlStats();
            long sequence = 0;
            foreach (var entry in Jsonl.Read(path, fileStats))
            {
                var line = (JsonElement?)entry;
                var sessionId = line.Get("session_id").StrOr(Path.GetFileNameWithoutExtension(path));
                var role = line.Get("role").AsString();
                if (role is not ("user" or "assistant") || line.Get("text").AsString() is not { } text) continue;
                Add(grouped, sessionId, new Message(line.Get("message_id").StrOr(Number(sequence)), role, text, sequence, line.Get("timestamp").StrOr("")));
                sources[sessionId] = path;
                sequence++;
            }
            stats["malformed"] += fileStats.Malformed;
            stats["partial_trailing"] += fileStats.PartialTrailing;
        }
        var documents = grouped.Select(pair => new Document("synthetic", "synthetic-proof", pair.Key, sources[pair.Key], pair.Value)).ToList();
        stats["sessions"] = documents.Count;
        return new FamilyResult(documents, stats);
    }

    static Dictionary<string, long> Stats(params string[] names) => names.ToDictionary(name => name, _ => 0L);

    static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    static void Add(Dictionary<string, List<Message>> sessions, string sessionId, Message message)
    {
        if (!sessions.TryGetValue(sessionId, out var messages)) sessions[sessionId] = messages = [];
        messages.Add(message);
    }

    // Same order as Python 3.12 Path.rglob: the root, then the children of each directory walked depth-first.
    internal static IEnumerable<string> Rglob(string root, string pattern)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var directory in WalkDirectories(root))
        {
            foreach (var file in Files(directory, pattern)) yield return file;
        }
    }

    static IEnumerable<string> WalkDirectories(string root)
    {
        yield return root;
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var children = SubDirectories(pending.Pop());
            foreach (var child in children) yield return child;
            for (var index = children.Count - 1; index >= 0; index--) pending.Push(children[index]);
        }
    }

    static List<string> SubDirectories(string directory)
    {
        try
        {
            return [.. new DirectoryInfo(directory).EnumerateDirectories()
                .Where(child => !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Select(child => child.FullName)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    static List<string> Files(string directory, string pattern)
    {
        try
        {
            return [.. Directory.EnumerateFiles(directory, pattern)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.Compiled | RegexOptions.RightToLeft)]
    private static partial Regex MyRegex();
}
