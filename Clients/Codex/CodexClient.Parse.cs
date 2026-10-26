using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using static SessionMemory.ParserHelpers;

namespace SessionMemory.Clients.Codex
{
    // Turns the Codex stores into one document per thread.
    sealed internal partial class CodexClient
    {
        // Rollouts also record context Codex injects at the start of user turns, which its SQLite store leaves out.
        // Only these tags are injected; others, such as question replies, review results and voice requests, are the user's.
        [GeneratedRegex(
            @"^\s*(?:<(environment_context|recommended_plugins|turn_aborted|external_codex_apps_open_page)>.*?</\1>" +
            @"|# AGENTS\.md instructions[^\n]*\s*<INSTRUCTIONS>.*?</INSTRUCTIONS>)\s*",
            RegexOptions.Singleline)]
        private static partial Regex LeadingContext();

        [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.Compiled | RegexOptions.RightToLeft)]
        private static partial Regex RolloutId();

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

        private static FamilyResult ReadSessions(string home)
        {
            var history = History(home);
            var sessions = new Dictionary<string, List<Message>>();
            var stats = Stats("sqlite_threads", "fallback_threads", "projection_without_items", "partial_trailing", "malformed");
            if (File.Exists(history)) ReadHistory(history, sessions, stats);
            foreach (var path in Rollouts(home)) AddRollout(path, sessions, stats);
            var titles = Titles(home);
            ImmutableArray<Document> documents =
            [
                .. sessions.Select(pair => new Document(FamilyName, "codex", pair.Key, $"{history}#thread_id={pair.Key}", [.. pair.Value])
                {
                    Title = titles.GetValueOrDefault(pair.Key, "")
                })
            ];
            return new FamilyResult(documents, stats);
        }

        private static void ReadHistory(string history, Dictionary<string, List<Message>> sessions, Dictionary<string, long> stats)
        {
            using var database = new SqliteReader(history);
            database.Execute("BEGIN");
            ReadItems(database, sessions, stats);
            stats["sqlite_threads"] = sessions.Count;
            stats["projection_without_items"] = Sql.IntOr(database.Query(
                "SELECT count(*) FROM thread_history_projection_state p WHERE NOT EXISTS (SELECT 1 FROM thread_items i WHERE i.thread_id=p.thread_id)").First()[0], 0);
            // Older Codex stores have no realtime table.
            if (database.HasTable("thread_realtime_items")) ReadRealtime(database, sessions, stats);
            database.Execute("ROLLBACK");
        }

        private static void ReadItems(SqliteReader database, Dictionary<string, List<Message>> sessions, Dictionary<string, long> stats)
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
                if (ItemContent(row[5] as string, json.RootElement) is { Text.Length: > 0 } content)
                    Add(sessions, Sql.Str(row[0]), new Message(Sql.StrOr(row[2], Sql.Str(row[1])), content.Role, content.Text, Sql.IntOr(row[3], 0), Transcript.IsoFromMs(Sql.Number(row[4]))));
            }
        }

        // Null when the item is agent text from a phase the index leaves out.
        private static (string Role, string Text)? ItemContent(string? itemType, JsonElement? item)
        {
            if (itemType == "userMessage") return ("user", item.Get("content").ExtractContent("text"));
            return item.Get("phase").PhaseAllowed() ? ("assistant", item.Get("text").AsString() ?? "") : null;
        }

        private static void ReadRealtime(SqliteReader database, Dictionary<string, List<Message>> sessions, Dictionary<string, long> stats)
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

        private static void AddRollout(string path, Dictionary<string, List<Message>> sessions, Dictionary<string, long> stats)
        {
            // Rollouts are only a fallback, so skip decompressing one whose session SQLite already holds.
            if (path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) && RolloutId().Match(Path.GetFileName(path)) is { Success: true } id
                                                                          && sessions.ContainsKey(id.Value)) return;
            var fileStats = new JsonlStats();
            var (sessionId, messages) = ReadRollout(path, fileStats);
            stats["partial_trailing"] += fileStats.PartialTrailing;
            stats["malformed"] += fileStats.Malformed;
            if (sessionId.Length == 0) sessionId = Path.GetFileNameWithoutExtension(path);
            if (sessions.ContainsKey(sessionId) || messages.Count == 0) return;
            sessions[sessionId] = [.. messages.Values];
            stats["fallback_threads"]++;
        }

        // The session id is empty when no session_meta line names it.
        private static (string SessionId, Dictionary<string, Message> Messages) ReadRollout(string path, JsonlStats fileStats)
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
                    case "response_item" when RolloutMessage(line, payload, messages.Count) is { } message:
                        messages[message.Id] = message;
                        break;
                }
            }
            return (sessionId, messages);
        }

        private static Message? RolloutMessage(JsonElement? line, JsonElement? payload, int count)
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
        private static Dictionary<string, string> Titles(string home)
        {
            var titles = new Dictionary<string, string>();
            if (File.Exists(SessionIndex(home))) AddIndexTitles(SessionIndex(home), titles);
            foreach (var state in States(home)) AddStateTitles(state, titles);
            return titles;
        }

        private static void AddIndexTitles(string index, Dictionary<string, string> titles)
        {
            foreach (var entry in Jsonl.Read(index, new JsonlStats(), "\"thread_name\""))
            {
                var line = (JsonElement?)entry;
                if (line.Get("id").AsString() is { } id && line.Get("thread_name").AsString() is { Length: > 0 } name) titles[id] = name;
            }
        }

        // A state database that cannot be opened or queried only loses its titles.
        private static void AddStateTitles(string state, Dictionary<string, string> titles)
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
    }
}
