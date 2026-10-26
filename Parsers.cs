using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace SessionMemory
{
    sealed internal record Family
    {
        public Family(string name, Func<ImmutableArray<string>> sources, Func<FamilyResult> parse)
        {
            Name = name;
            Sources = sources;
            Parse = parse;
        }

        public string Name { get; }

        // Files whose size and timestamp decide whether the family must be parsed again.
        public Func<ImmutableArray<string>> Sources { get; }

        public Func<FamilyResult> Parse { get; }
    }

    /// <summary>Reads each assistant's local transcript store, mirroring the original Python bridge.</summary>
    internal static partial class Parsers
    {
        public static ImmutableArray<Family> All(string home, string root) =>
        [
            Synthetic(root),
            new("codex", () => CodexSources(home), () => Codex(home)),
            new("claude", () => [.. Rglob(ClaudeRoot(home), "*.jsonl")], () => Claude(home)),
            new("copilot", () => CopilotSources(home), () => Copilot(home)),
            new("vscode", () => ChatFiles(CodeUser(home)), () => VsCode(home))
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
                ["a2", "assistant", "Correction: the current retry limit is 5 and supersedes 2."]
            ];
            File.WriteAllLines(path, messages.Select(message => JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["session_id"] = Indexer.CanaryId,
                ["message_id"] = message[0],
                ["role"] = message[1],
                ["text"] = message[2]
            })));
        }

        internal static string CodexHistory(string home) => Path.Combine(home, ".codex", "thread_history_1.sqlite");

        private static string ClaudeRoot(string home) => Path.Combine(home, ".claude", "projects");

        private static string CopilotRoot(string home) => Path.Combine(home, ".copilot");

        private static readonly string[] CodexFolders = ["sessions", "archived_sessions"];

        private static readonly string[] CodexRolloutPatterns = ["*.jsonl", "*.jsonl.zst"];

        // Codex compresses older rollouts to .jsonl.zst; some sessions survive only in that form.
        private static IEnumerable<string> CodexRollouts(string home) =>
            CodexFolders
                .SelectMany(folder => CodexRolloutPatterns.SelectMany(pattern => Rglob(Path.Combine(home, ".codex", folder), pattern)));

        private static readonly Regex RolloutId = MyRegex();

        // Rollouts also record context Codex injects at the start of user turns, which its SQLite store leaves out.
        // Only these tags are injected; others, such as question replies, review results and voice requests, are the user's.
        [GeneratedRegex(
            @"^\s*(?:<(environment_context|recommended_plugins|turn_aborted|external_codex_apps_open_page)>.*?</\1>" +
            @"|# AGENTS\.md instructions[^\n]*\s*<INSTRUCTIONS>.*?</INSTRUCTIONS>)\s*",
            RegexOptions.Singleline)]
        private static partial Regex LeadingContext();

        public static string StripInjectedContext(string text)
        {
            string previous;
            do
            {
                previous = text;
                text = LeadingContext().Replace(text, "", 1);
            } while (text != previous);
            return text;
        }

        private static ImmutableArray<string> CodexSources(string home) =>
            [.. DatabaseFiles(CodexHistory(home)), .. CodexRollouts(home), CodexIndex(home), .. CodexStates(home).SelectMany(DatabaseFiles)];

        private static string CodexIndex(string home) => Path.Combine(home, ".codex", "session_index.jsonl");

        // The version suffix (state_5) changes with Codex releases; the newest file is current.
        private static IEnumerable<string> CodexStates(string home) =>
            Directory.Exists(Path.Combine(home, ".codex"))
                ? Directory.EnumerateFiles(Path.Combine(home, ".codex"), "state_*.sqlite").Order(StringComparer.Ordinal).TakeLast(1)
                : [];

        private static ImmutableArray<string> CopilotSources(string home) =>
        [
            .. DatabaseFiles(Path.Combine(CopilotRoot(home), "data.db")),
            .. DatabaseFiles(Path.Combine(CopilotRoot(home), "session-store.db")),
            .. CopilotEventFiles(home),
            .. CopilotEventFiles(home).Select(WorkspaceFile).Where(File.Exists)
        ];

        private static string WorkspaceFile(string eventFile) => Path.Combine(Path.GetDirectoryName(eventFile)!, "workspace.yaml");

        private static string[] DatabaseFiles(string path) => [path, path + "-wal"];

        internal static IEnumerable<string> CopilotEventFiles(string home)
        {
            var root = Path.Combine(CopilotRoot(home), "session-state");
            return Directory.Exists(root)
                ? Directory.EnumerateDirectories(root).Select(directory => Path.Combine(directory, "events.jsonl")).Where(File.Exists)
                : [];
        }

        private static ImmutableArray<string> SyntheticFiles(string root)
        {
            var directory = Path.Combine(root, "synthetic");
            return Directory.Exists(directory) ? [.. Directory.EnumerateFiles(directory, "*.jsonl")] : [];
        }

        private static FamilyResult Codex(string home)
        {
            var history = CodexHistory(home);
            var sessions = new Dictionary<string, List<Message>>();
            var stats = Stats("sqlite_threads", "fallback_threads", "projection_without_items", "partial_trailing", "malformed");
            if (File.Exists(history)) CodexReadHistory(history, sessions, stats);
            foreach (var path in CodexRollouts(home)) CodexAddRollout(path, sessions, stats);
            var titles = CodexTitles(home);
            ImmutableArray<Document> documents =
            [
                .. sessions.Select(pair => new Document("codex", "codex", pair.Key, $"{history}#thread_id={pair.Key}", [.. pair.Value])
                {
                    Title = titles.GetValueOrDefault(pair.Key, "")
                })
            ];
            return new FamilyResult(documents, stats);
        }

        private static void CodexReadHistory(string history, Dictionary<string, List<Message>> sessions, Dictionary<string, long> stats)
        {
            using var database = new SqliteReader(history);
            database.Execute("BEGIN");
            CodexReadItems(database, sessions, stats);
            stats["sqlite_threads"] = sessions.Count;
            stats["projection_without_items"] = Sql.IntOr(database.Query(
                "SELECT count(*) FROM thread_history_projection_state p WHERE NOT EXISTS (SELECT 1 FROM thread_items i WHERE i.thread_id=p.thread_id)").First()[0], 0);
            // Older Codex stores have no realtime table.
            if (database.HasTable("thread_realtime_items")) CodexReadRealtime(database, sessions, stats);
            database.Execute("ROLLBACK");
        }

        private static void CodexReadItems(SqliteReader database, Dictionary<string, List<Message>> sessions, Dictionary<string, long> stats)
        {
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
                if (CodexItemContent(row[5] as string, json.RootElement) is { Text.Length: > 0 } content)
                    Add(sessions, Sql.Str(row[0]), new Message(Sql.StrOr(row[2], Sql.Str(row[1])), content.Role, content.Text, Sql.IntOr(row[3], 0), Transcript.IsoFromMs(Sql.Number(row[4]))));
            }
        }

        // Null when the item is agent text from a phase the index leaves out.
        private static (string Role, string Text)? CodexItemContent(string? itemType, JsonElement? item)
        {
            if (itemType == "userMessage") return ("user", item.Get("content").ExtractContent("text"));
            return item.Get("phase").PhaseAllowed() ? ("assistant", item.Get("text").AsString() ?? "") : null;
        }

        private static void CodexReadRealtime(SqliteReader database, Dictionary<string, List<Message>> sessions, Dictionary<string, long> stats)
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

        private static void CodexAddRollout(string path, Dictionary<string, List<Message>> sessions, Dictionary<string, long> stats)
        {
            // Rollouts are only a fallback, so skip decompressing one whose session SQLite already holds.
            if (path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) && RolloutId.Match(Path.GetFileName(path)) is { Success: true } id
                                                                          && sessions.ContainsKey(id.Value)) return;
            var fileStats = new JsonlStats();
            var (sessionId, messages) = CodexReadRollout(path, fileStats);
            stats["partial_trailing"] += fileStats.PartialTrailing;
            stats["malformed"] += fileStats.Malformed;
            if (sessionId.Length == 0) sessionId = Path.GetFileNameWithoutExtension(path);
            if (sessions.ContainsKey(sessionId) || messages.Count == 0) return;
            sessions[sessionId] = [.. messages.Values];
            stats["fallback_threads"]++;
        }

        // The session id is empty when no session_meta line names it.
        private static (string SessionId, Dictionary<string, Message> Messages) CodexReadRollout(string path, JsonlStats fileStats)
        {
            var sessionId = "";
            var messages = new Dictionary<string, Message>();
            foreach (var entry in Jsonl.Read(path, fileStats, "\"session_meta\"", "\"response_item\""))
            {
                var line = (JsonElement?)entry;
                var payload = line.Get("payload");
                var type = line.Get("type").AsString();
                switch (type)
                {
                    case "session_meta":
                        sessionId = payload.Get("id").StrOr(payload.Get("session_id").StrOr(sessionId));
                        break;
                    case "response_item" when CodexRolloutMessage(line, payload, messages.Count) is { } message:
                        messages[message.Id] = message;
                        break;
                }
            }
            return (sessionId, messages);
        }

        private static Message? CodexRolloutMessage(JsonElement? line, JsonElement? payload, int count)
        {
            if (payload.Get("type").AsString() != "message") return null;
            var role = payload.Get("role").AsString();
            if (role is not ("user" or "assistant")) return null;
            if (role == "assistant" && !payload.Get("phase").PhaseAllowed()) return null;
            var text = payload.Get("content").ExtractContent(role == "user" ? "input_text" : "output_text");
            if (role == "user") text = StripInjectedContext(text);
            if (text.Length == 0) return null;
            var messageId = payload.Get("id").StrOr(line.Get("ordinal").StrOr(Number(count)));
            return new Message(messageId, role, text, line.Get("ordinal").IntOr(count), line.Get("timestamp").StrOr(""));
        }

        // Thread names come from the session index and the state database; titles are optional, so a schema change only loses them.
        private static Dictionary<string, string> CodexTitles(string home)
        {
            var titles = new Dictionary<string, string>();
            if (File.Exists(CodexIndex(home))) CodexAddIndexTitles(CodexIndex(home), titles);
            foreach (var state in CodexStates(home)) CodexAddStateTitles(state, titles);
            return titles;
        }

        private static void CodexAddIndexTitles(string index, Dictionary<string, string> titles)
        {
            foreach (var entry in Jsonl.Read(index, new JsonlStats(), "\"thread_name\""))
            {
                var line = (JsonElement?)entry;
                if (line.Get("id").AsString() is { } id && line.Get("thread_name").AsString() is { Length: > 0 } name) titles[id] = name;
            }
        }

        // A state database that cannot be opened or queried only loses its titles.
        private static void CodexAddStateTitles(string state, Dictionary<string, string> titles)
        {
            foreach (var row in QueryOnce(state, "SELECT id, name FROM threads WHERE name IS NOT NULL AND name <> ''").GetValueOrDefault([]))
                titles[Sql.Str(row[0])] = Sql.Str(row[1]);
        }

        // Opens the database for one materialized query and closes it again.
        private static Result<ImmutableArray<object?[]>, string> QueryOnce(string path, string sql) =>
            SqliteReader.Open(path).Bind(database =>
            {
                using (database) return database.TryQuery(sql);
            });

        private static FamilyResult Claude(string home)
        {
            var documents = new List<Document>();
            var stats = Stats("files", "parent_files", "subagent_files", "malformed", "partial_trailing");
            foreach (var path in Rglob(ClaudeRoot(home), "*.jsonl"))
            {
                stats["files"]++;
                var subagent = path.Split(Path.DirectorySeparatorChar).Any(part => part.Equals("subagents", StringComparison.OrdinalIgnoreCase));
                stats[subagent ? "subagent_files" : "parent_files"]++;
                var client = subagent ? "claude-code-subagent" : "claude-code";
                var fileStats = new JsonlStats();
                var (sessions, title) = ClaudeReadFile(path, subagent, fileStats);
                stats["malformed"] += fileStats.Malformed;
                stats["partial_trailing"] += fileStats.PartialTrailing;
                foreach (var (sessionId, messages) in sessions)
                {
                    documents.Add(new Document("claude", client, sessionId, path, [.. messages.Values.OrderBy(message => message.Sequence)])
                    {
                        Title = title
                    });
                }
            }
            return new FamilyResult([.. documents], stats);
        }

        private static (Dictionary<string, Dictionary<string, Message>> Sessions, string Title) ClaudeReadFile(string path, bool subagent, JsonlStats fileStats)
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
                if (ClaudeMessage(line, type, sequence) is not { } message) continue;
                var sessionId = ClaudeSessionId(line, stem, subagent);
                if (!sessions.TryGetValue(sessionId, out var messages)) sessions[sessionId] = messages = [];
                messages[message.Id] = message;
                sequence++;
            }
            return (sessions, customTitle ?? aiTitle ?? "");
        }

        private static Message? ClaudeMessage(JsonElement? line, string? type, long sequence)
        {
            if (type is not ("user" or "assistant") || line.Get("isMeta") is { ValueKind: JsonValueKind.True }) return null;
            var message = line.Get("message");
            var text = (message is { ValueKind: JsonValueKind.Object } ? message.Get("content") : null).ExtractContent("text");
            if (text.Length == 0) return null;
            var messageId = line.Get("uuid").StrOr(message.Get("id").StrOr(Number(sequence)));
            return new Message(messageId, type, text, sequence, line.Get("timestamp").StrOr(""));
        }

        // Each subagent transcript is its own session, even when it shares the parent's session id.
        private static string ClaudeSessionId(JsonElement? line, string stem, bool subagent)
        {
            var sessionId = line.Get("sessionId").StrOr(stem);
            return subagent ? $"{sessionId}:subagent:{stem}" : sessionId;
        }

        private static FamilyResult ParseSynthetic(string root)
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
            ImmutableArray<Document> documents = [.. grouped.Select(pair => new Document("synthetic", "synthetic-proof", pair.Key, sources[pair.Key], [.. pair.Value]))];
            stats["sessions"] = documents.Length;
            return new FamilyResult(documents, stats);
        }

        private static Dictionary<string, long> Stats(params string[] names) => names.ToDictionary(name => name, _ => 0L);

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static void Add(Dictionary<string, List<Message>> sessions, string sessionId, Message message)
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

        private static IEnumerable<string> WalkDirectories(string root)
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

        private static List<string> SubDirectories(string directory) =>
            Listing(() => new DirectoryInfo(directory).EnumerateDirectories()
                .Where(child => !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Select(child => child.FullName));

        private static List<string> Files(string directory, string pattern) => Listing(() => Directory.EnumerateFiles(directory, pattern));

        // A directory that vanished or cannot be read lists nothing, so the walk goes on without it.
        private static List<string> Listing(Func<IEnumerable<string>> entries) => Attempt.Io(() => entries().ToList()).GetValueOrDefault([]);

        [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.Compiled | RegexOptions.RightToLeft)]
        private static partial Regex MyRegex();
    }
}
