using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SessionMemory
{
    internal static class ConversationSearch
    {
        private const int SchemaVersion = 4;
        private static readonly Regex Word = new(@"[\p{L}\p{N}]+(?:\.[\p{L}\p{N}]+)*", RegexOptions.Compiled);
        private static readonly Regex Component = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
        private static readonly Regex Camel = new(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);
        private static readonly Regex ResourceUri = new(@"https?://[^\s<>\""'`]+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly HashSet<string> Stop = new(("a an and are as at be been being but by can could did do does for from had has have he her hers him his i if in into is it its may me might my no nor not of on or our ours she should so than that the their theirs them then there these they this those through to too under up us was we were what when where which who whom why will with would you your yours").Split(' '), StringComparer.OrdinalIgnoreCase);

        private sealed record Message(int Number, string Role, string Timestamp, string Text);

        private sealed record Window(string File, ImmutableArray<Message> Messages, int Rank, DateTimeOffset Time);

        private const string Schema = """
                                      CREATE TABLE IF NOT EXISTS files (name TEXT PRIMARY KEY, sha TEXT NOT NULL, title TEXT NOT NULL);
                                      CREATE VIRTUAL TABLE IF NOT EXISTS windows USING fts5(body, file UNINDEXED, messages UNINDEXED, tokenize="porter unicode61 tokenchars '.'");
                                      CREATE TABLE IF NOT EXISTS window_sources (id INTEGER PRIMARY KEY, file TEXT NOT NULL);
                                      CREATE INDEX IF NOT EXISTS window_sources_file ON window_sources(file);
                                      CREATE TABLE IF NOT EXISTS nav_messages (id INTEGER PRIMARY KEY, file TEXT NOT NULL, number INTEGER NOT NULL, role TEXT NOT NULL, stamp TEXT NOT NULL, utc_ticks INTEGER, text TEXT NOT NULL, folded TEXT NOT NULL, UNIQUE(file,number,role,stamp));
                                      CREATE INDEX IF NOT EXISTS nav_messages_file ON nav_messages(file);
                                      CREATE INDEX IF NOT EXISTS nav_messages_time_file ON nav_messages(utc_ticks,file);
                                      CREATE TABLE IF NOT EXISTS nav_refs (message_id INTEGER NOT NULL, target TEXT NOT NULL, UNIQUE(message_id,target));
                                      CREATE INDEX IF NOT EXISTS nav_refs_target ON nav_refs(target);
                                      """;

        public static ImmutableArray<JsonObject> Retrieve(string root, string query, IReadOnlyDictionary<string, DocumentMeta> documents,
            IReadOnlySet<string> visible, IEnumerable<JsonObject> ranked, int maxSessions, bool latest,
            IReadOnlySet<string>? indexable = null, string? excludedTopic = null)
        {
            var allowed = indexable ?? visible;
            if ((maxSessions <= 0 && visible.Count > 0) || (visible.Count == 0 && allowed.Count > 0)) return [];
            using var db = OpenRoot(root, visible.Count > 0);
            if (db is null) return [];
            Refresh(db, root, documents, allowed);
            if (visible.Count == 0) return [];
            db.Execute("CREATE TEMP TABLE visible_files(name TEXT PRIMARY KEY)");
            foreach (var file in visible.Where(allowed.Contains)) db.Execute("INSERT OR IGNORE INTO visible_files VALUES (?)", file);

            var terms = Tokens(query).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var candidates = new List<Window>();
            if (terms.Length > 0)
            {
                var match = string.Join(" OR ", terms.Select(term => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
                foreach (var row in db.Query("SELECT windows.file, windows.messages FROM windows JOIN visible_files ON windows.file=visible_files.name WHERE windows MATCH ? ORDER BY bm25(windows)", match))
                {
                    var messages = JsonSerializer.Deserialize<ImmutableArray<Message>>((string)row[1]!);
                    if (messages.IsDefaultOrEmpty || (!string.IsNullOrWhiteSpace(excludedTopic)
                                                      && messages.Any(m => m.Text.Contains(excludedTopic, StringComparison.OrdinalIgnoreCase)))) continue;
                    var time = MatchingTime(messages, terms);
                    candidates.Add(new Window((string)row[0]!, messages, candidates.Count + 1, time));
                    if (candidates.Count >= Math.Clamp((long)maxSessions * 20, 200, 1000)) break;
                }
            }
            var fused = candidates.GroupBy(Key, StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => (window: g.First(), score: 1d / (60 + g.First().Rank)), StringComparer.Ordinal);
            var cachedFiles = new Dictionary<string, (List<ImmutableArray<Message>> Windows, string Hash)>(StringComparer.Ordinal);
            var semanticKeys = new HashSet<string>(StringComparer.Ordinal);
            var semanticRank = 0;
            foreach (var hit in ranked)
            {
                semanticRank++;
                var file = hit["file"].AsString() ?? "";
                var text = hit["text"].AsString() ?? "";
                var passage = hit["semantic_passage"].AsString();
                if (!visible.Contains(file) || !allowed.Contains(file) || (text.Length == 0 && passage is null)) continue;
                if (!cachedFiles.TryGetValue(file, out var cached))
                {
                    if (cachedFiles.Count >= 200) continue;
                    var rows = db.Query("SELECT windows.messages, files.sha FROM window_sources JOIN windows ON windows.rowid=window_sources.id JOIN files ON files.name=window_sources.file JOIN visible_files ON window_sources.file=visible_files.name WHERE window_sources.file=?", file).ToArray();
                    cached = (rows.Select(row => JsonSerializer.Deserialize<ImmutableArray<Message>>((string)row[0]!)).Where(ms => !ms.IsDefaultOrEmpty).ToList(),
                        rows.Length == 0 ? "" : (string)rows[0][1]!);
                    cachedFiles[file] = cached;
                }
                int? sourceNumber = null;
                if (passage is not null)
                {
                    if (cached.Hash.Length == 0 || !documents.TryGetValue(file, out var meta)) continue;
                    var current = new Dictionary<string, DocumentMeta> { [file] = meta with { Sha256 = cached.Hash } };
                    if (!SemanticPassages.TrySourceFile(passage, current, out var origin, out sourceNumber)
                        || origin != file || sourceNumber is null) continue;
                }
                var segments = sourceNumber is null ? Compact.Segments(text) : [];
                var verified = cached.Windows.Where(ms => string.IsNullOrWhiteSpace(excludedTopic)
                                                   || !ms.Any(m => m.Text.Contains(excludedTopic, StringComparison.OrdinalIgnoreCase)))
                    .Select(ms => (messages: ms, matching: ms.Select((m, i) => (message: m, index: i))
                        .Where(pair => sourceNumber is not null ? pair.message.Number == sourceNumber
                            : segments.Any(s => Verifies(s, pair.message))).Select(pair => pair.index).ToArray()))
                    .Where(w => w.matching.Length > 0)
                    .OrderByDescending(w => w.matching.Contains(w.messages.Length / 2)).FirstOrDefault();
                if (verified.matching is null) continue;
                var time = verified.matching.Select(i => DateTimeOffset.TryParse(verified.messages[i].Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : DateTimeOffset.MinValue).Max();
                var candidate = new Window(file, verified.messages, semanticRank, time);
                var key = Key(candidate);
                if (!semanticKeys.Add(key)) continue;
                if (fused.TryGetValue(key, out var old))
                    fused[key] = (old.window with
                    {
                        Time = old.window.Time > time ? old.window.Time : time
                    }, old.score + 1d / (60 + semanticRank));
                else fused[key] = (candidate, 1d / (60 + semanticRank));
            }
            candidates = fused.Values.OrderByDescending(c => c.score).ThenBy(c => c.window.Rank)
                .Select((c, i) => c.window with
                {
                    Rank = i + 1
                }).ToList();
            if (latest)
            {
                var chronological = candidates.OrderByDescending(c => c.Time).ThenBy(c => c.Rank)
                    .Select((candidate, i) => (candidate, rank: i + 1)).ToDictionary(pair => pair.candidate, pair => pair.rank);
                candidates = candidates.OrderByDescending(c => 1d / (60 + c.Rank) + 1d / (60 + chronological[c])).ThenByDescending(c => c.Time).ThenBy(c => c.Rank).ToList();
            }

            var output = ImmutableArray.CreateBuilder<JsonObject>();
            var seen = new Dictionary<string, HashSet<(int, string, string)>>(StringComparer.Ordinal);
            var metadata = documents.ToDictionary(p => p.Key, p => p.Value);
            foreach (var candidate in candidates)
            {
                if (!seen.TryGetValue(candidate.File, out var keys))
                {
                    if (seen.Count >= maxSessions) continue;
                    keys = [];
                }
                if (candidate.Messages.Any(m => keys.Contains((m.Number, m.Role, m.Timestamp)))) continue;
                var hit = Render(candidate, terms, 1500);
                var trial = output.ToImmutable().Add(hit);
                if (Indexer.RenderText("cli", trial, metadata).Length > 12000) continue;
                output.Add(hit);
                seen[candidate.File] = keys;
                foreach (var m in candidate.Messages) keys.Add((m.Number, m.Role, m.Timestamp));
                if (output.Count == maxSessions) break;
            }
            return output.ToImmutable();
        }

        private static string Key(Window window) => window.File + "\0" + string.Join("\0", window.Messages.Select(m => $"{m.Number}\t{m.Role}\t{m.Timestamp}"));

        private static bool Verifies(Compact.Segment source, Message message)
        {
            var fragment = Regex.Replace(source.Text, @"\s+", " ").Trim();
            var original = Regex.Replace(message.Text, @"\s+", " ").Trim();
            return fragment.Length >= 20 && original.Length >= 20
                                         && (original.Contains(fragment, StringComparison.OrdinalIgnoreCase) || fragment.Contains(original, StringComparison.OrdinalIgnoreCase))
                   || source.Number > 0 && source.Number == message.Number && source.Timestamp.Length > 0
                   && source.Timestamp == message.Timestamp;
        }

        public static void Prepare(string root, IReadOnlyDictionary<string, DocumentMeta> documents,
            IReadOnlySet<string> indexable, Action<int, int>? progress = null)
        {
            using var db = OpenRoot(root, indexable.Count > 0);
            if (db is null)
            {
                progress?.Invoke(0, 0);
                return;
            }
            Refresh(db, root, documents, indexable, progress);
        }

        private static SqliteReader? OpenRoot(string root, bool create)
        {
            var state = Path.Combine(root, "state");
            var path = Path.Combine(state, "conversation-search.sqlite");
            if (!create && !File.Exists(path)) return null;
            Directory.CreateDirectory(state);
            if ((File.GetAttributes(state) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid cache directory.");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid cache file.");
            return OpenCache(path);
        }

        private static SqliteReader OpenCache(string path)
        {
            var db = SqliteReader.Create(path);
            try
            {
                db.Execute("BEGIN IMMEDIATE");
                var version = Convert.ToInt32(db.Query("PRAGMA user_version").Single()[0], CultureInfo.InvariantCulture);
                if (version != SchemaVersion)
                {
                    db.Execute("DROP TABLE IF EXISTS windows");
                    db.Execute("DROP TABLE IF EXISTS files");
                    db.Execute("DROP TABLE IF EXISTS window_sources");
                    db.Execute("DROP TABLE IF EXISTS nav_refs");
                    db.Execute("DROP TABLE IF EXISTS nav_messages");
                }
                foreach (var statement in Schema.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) db.Execute(statement);
                db.Execute($"PRAGMA user_version = {SchemaVersion}");
                _ = db.Query("SELECT name,sha,title FROM files LIMIT 0").ToArray();
                _ = db.Query("SELECT file,messages FROM windows LIMIT 0").ToArray();
                _ = db.Query("SELECT id,file FROM window_sources LIMIT 0").ToArray();
                _ = db.Query("SELECT id,file,number,role,stamp,utc_ticks,text,folded FROM nav_messages LIMIT 0").ToArray();
                _ = db.Query("SELECT message_id,target FROM nav_refs LIMIT 0").ToArray();
                db.Execute("COMMIT");
                return db;
            }
            catch
            {
                db.Dispose();
                throw;
            }
        }

        private static void Refresh(SqliteReader db, string root, IReadOnlyDictionary<string, DocumentMeta> documents, IReadOnlySet<string> allowed, Action<int, int>? progress = null)
        {
            db.Execute("BEGIN IMMEDIATE");
            try
            {
                var existing = db.Query("SELECT name,sha,title FROM files").ToDictionary(r => (string)r[0]!, r => ((string)r[1]!, (string)r[2]!), StringComparer.Ordinal);
                var nextId = (long)db.Query("SELECT COALESCE(MAX(id),0)+1 FROM window_sources").Single()[0]!;
                foreach (var file in existing.Keys.Where(f => !allowed.Contains(f) || !documents.ContainsKey(f)).ToArray()) Delete(db, file);
                var files = allowed.Where(documents.ContainsKey).ToArray();
                var done = 0;
                progress?.Invoke(0, files.Length);
                foreach (var file in files)
                {
                    try
                    {
                        var meta = documents[file];
                        string hash;
                        string text;
                        try
                        {
                            var corpus = Path.GetFullPath(Path.Combine(root, "corpus"));
                            if (Path.GetFileName(file) != file || (File.GetAttributes(corpus) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid corpus path.");
                            var path = Path.Combine(corpus, file);
                            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid corpus path.");
                            text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
                            hash = Transcript.Sha256(text);
                            if (meta.Sha256.Length > 0 && !hash.Equals(meta.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Corpus hash mismatch.");
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                        {
                            if (existing.ContainsKey(file)) Delete(db, file);
                            continue;
                        }
                        if (existing.TryGetValue(file, out var prior) && prior.Item1 == hash && prior.Item2 == meta.Title)
                            continue;
                        var messages = NavigationMessages(text);
                        if (existing.ContainsKey(file)) Delete(db, file);
                        db.Execute("INSERT INTO files(name,sha,title) VALUES (?,?,?)", file, hash, meta.Title);
                        for (var i = 0; i < messages.Length; i++)
                        {
                            var window = messages.Skip(Math.Max(0, i - 1)).Take(Math.Min(messages.Length, i + 2) - Math.Max(0, i - 1)).ToImmutableArray();
                            var body = string.Join("\n", window.Select(m => m.Text + "\n" + meta.Title + " " + m.Timestamp + " " + DateWords(m.Timestamp)));
                            var id = nextId++;
                            db.Execute("INSERT INTO windows(rowid,body,file,messages) VALUES (?,?,?,?)", id, body + " " + string.Join(' ', Tokens(body)), file, JsonSerializer.Serialize(window));
                            db.Execute("INSERT INTO window_sources(id,file) VALUES (?,?)", id, file);
                        }
                        IndexNavigation(db, file, messages);
                    }
                    finally { progress?.Invoke(++done, files.Length); }
                }
                db.Execute("COMMIT");
            }
            catch
            {
                db.Execute("ROLLBACK");
                throw;
            }
        }

        private static void Delete(SqliteReader db, string file)
        {
            db.Execute("DELETE FROM windows WHERE rowid IN (SELECT id FROM window_sources WHERE file=?)", file);
            db.Execute("DELETE FROM window_sources WHERE file=?", file);
            db.Execute("DELETE FROM nav_refs WHERE message_id IN (SELECT id FROM nav_messages WHERE file=?)", file);
            db.Execute("DELETE FROM nav_messages WHERE file=?", file);
            db.Execute("DELETE FROM files WHERE name=?", file);
        }

        public static string FoldNavigationText(string text) => text.ToUpperInvariant();

        public static string? NormalizeNavigationUri(string value)
        {
            var parts = Regex.Match(value, @"\A(?<scheme>https?)://(?<authority>[^/?#]+)(?<rest>.*)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!parts.Success || value.Any(char.IsWhiteSpace) || parts.Groups["authority"].Value.IndexOfAny(['@', '\\']) >= 0
                || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Host.Length == 0) return null;
            return parts.Groups["scheme"].Value.ToLowerInvariant() + "://"
                   + parts.Groups["authority"].Value.ToLowerInvariant() + parts.Groups["rest"].Value;
        }

        public static IEnumerable<string> NavigationUris(string text)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in ResourceUri.Matches(text))
            {
                var value = match.Value;
                while (value.EndsWith(')') && value.Count(c => c == ')') > value.Count(c => c == '(')) value = value[..^1];
                var target = NormalizeNavigationUri(value);
                if (target is not null && seen.Add(target)) yield return target;
            }
        }

        internal static IEnumerable<int> NavigationUriPositions(string text, string target)
        {
            foreach (Match match in ResourceUri.Matches(text))
            {
                var value = match.Value;
                while (value.EndsWith(')') && value.Count(c => c == ')') > value.Count(c => c == '(')) value = value[..^1];
                if (NormalizeNavigationUri(value) == target) yield return match.Index;
            }
        }

        private static void IndexNavigation(SqliteReader db, string file, ImmutableArray<Message> messages)
        {
            db.Execute("DELETE FROM nav_refs WHERE message_id IN (SELECT id FROM nav_messages WHERE file=?)", file);
            db.Execute("DELETE FROM nav_messages WHERE file=?", file);
            foreach (var message in messages.GroupBy(m => m.Number).Where(group => group.Count() == 1).Select(group => group.Single()))
            {
                var text = message.Text;
                var stamp = message.Timestamp;
                object? ticks = Regex.IsMatch(stamp, @"(?:[zZ]|[+-]\d{2}:?\d{2})$")
                                && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? date.UtcTicks : null;
                db.Execute("INSERT INTO nav_messages(file,number,role,stamp,utc_ticks,text,folded) VALUES (?,?,?,?,?,?,?)",
                    file, message.Number, message.Role, stamp, ticks, text, FoldNavigationText(text));
                var id = (long)db.Query("SELECT last_insert_rowid()").Single()[0]!;
                foreach (var target in NavigationUris(text))
                    db.Execute("INSERT INTO nav_refs(message_id,target) VALUES (?,?)", id, target);
            }
        }

        private static ImmutableArray<Message> NavigationMessages(string text) =>
            [.. Transcript.SourceMessages(text).Select(message => new Message(message.Number, message.Role, message.Timestamp, message.Text))];

        private static IEnumerable<string> Tokens(string text)
        {
            foreach (Match match in Word.Matches(text))
            {
                var word = match.Value;
                if (!Stop.Contains(word)) yield return word.ToLowerInvariant();
                foreach (Match part in Component.Matches(Camel.Replace(word, " ")))
                    if (!part.Value.Equals(word, StringComparison.OrdinalIgnoreCase) && !Stop.Contains(part.Value))
                        yield return part.Value.ToLowerInvariant();
            }
        }

        private static string DateWords(string timestamp) =>
            DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date.ToString("MMMM MMM d yyyy M dddd ddd", CultureInfo.InvariantCulture) : "";

        private static DateTimeOffset MatchingTime(ImmutableArray<Message> messages, string[] terms) =>
            messages.Where(m => Tokens(m.Text).Intersect(terms, StringComparer.OrdinalIgnoreCase).Any())
                .Select(m => DateTimeOffset.TryParse(m.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : DateTimeOffset.MinValue)
                .DefaultIfEmpty(DateTimeOffset.MinValue).Max();

        private static JsonObject Render(Window window, string[] terms, int budget)
        {
            var text = new StringBuilder();
            var relevant = window.Messages.OrderByDescending(m => Tokens(m.Text).Intersect(terms, StringComparer.OrdinalIgnoreCase).Count()).ToArray();
            var pieces = new Dictionary<Message, string>();
            var remaining = budget;
            foreach (var m in relevant)
            {
                var allowance = Math.Max(0, remaining / (relevant.Length - pieces.Count));
                var snippet = Clip(m.Text, terms, Math.Max(0, allowance - 100));
                pieces[m] = snippet;
                remaining -= snippet.Length + 100;
            }
            foreach (var m in window.Messages)
                text.Append($"## Message {m.Number:000000} part 1/1\nrole: {m.Role}\ntimestamp: {m.Timestamp}\ntext_length: {pieces[m].Length}\ntext:\n").Append(pieces[m]).Append('\n');
            var first = relevant[0];
            return new JsonObject
            {
                ["file"] = window.File,
                ["text"] = text.ToString(),
                ["message_number"] = first.Number,
                ["role"] = first.Role,
                ["evidence_timestamp"] = first.Timestamp,
                ["selection_reason"] = "conversation window",
                ["evidence_messages"] = new JsonArray(window.Messages.Select(m => (JsonNode?)JsonValue.Create(m.Number)).ToArray())
            };
        }

        private static string Clip(string text, string[] terms, int limit)
        {
            if (text.Length <= limit) return text;
            if (limit < 80) return "[... omitted ...]";
            var matches = Word.Matches(text).Cast<Match>().Where(m => Tokens(m.Value).Intersect(terms, StringComparer.OrdinalIgnoreCase).Any()).ToArray();
            var best = 0;
            var most = 0;
            var right = 0;
            for (var left = 0; left < matches.Length; left++)
            {
                while (right < matches.Length && matches[right].Index < matches[left].Index + limit / 2) right++;
                if (right - left <= most) continue;
                most = right - left;
                best = matches[left].Index;
            }
            var proposed = Math.Max(0, best - limit / 3);
            var starts = Regex.Matches(text[..proposed], @"\n|[.!?](?=\s)").Cast<Match>().ToArray();
            var start = starts.Length == 0 ? -1 : starts[^1].Index + 1;
            if (start < 0 || proposed - start > limit / 3)
            {
                start = proposed == 0 ? 0 : Math.Max(0, text.LastIndexOf(' ', proposed) + 1);
            }
            var end = Math.Min(text.Length, start + limit - 40);
            if (end < text.Length)
            {
                var boundary = text.LastIndexOfAny(['\n', '\t', ' '], end);
                if (boundary > start) end = boundary + 1;
                else return "[... oversized text omitted ...]";
            }
            return (start > 0 ? "[... earlier text omitted ...]\n" : "") + text[start..end] + (end < text.Length ? "\n[... later text omitted ...]" : "");
        }
    }
}
