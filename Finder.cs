using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SessionMemory
{
    /// <summary>
    /// Exact-term search over the raw stores. Unlike the semantic index it also covers tool inputs such as edits,
    /// commands and page updates, whose full text would nearly triple the index. Tool outputs stay out as noise unless asked for.
    /// </summary>
    internal static partial class Finder
    {
        private const string CopilotOutput = "tool.execution_complete";
        private static readonly string[] CopilotTypes = ["user.message", "assistant.message", "tool.execution_start"];
        private static readonly Regex VsCodeTimestamp = MyRegex();
        private const int WindowChars = 300;

        // With a session ID (or prefix), lists every matching excerpt in that session instead of one per session.
        // since keeps only records at or after that time; outputs adds Copilot CLI tool results to what is searched.
        public static int Run(string root, string home, IReadOnlyList<string> terms, int top, bool json, string? only = null, bool partial = false, bool includeCurrent = false,
            DateTimeOffset? since = null, bool outputs = false)
        {
            var stopwatch = Stopwatch.StartNew();
            var manifest = Store.Load<Manifest>(Path.Combine(root, "state", "manifest.json"));
            var documents = manifest?.Documents ?? [];
            var work = WorkItems(home, documents, outputs);
            // Load balancing hands files out one at a time, so a few large logs don't pile up on one core.
            var found = new Dictionary<(string Client, string SessionId), Found>();
            var scans = Partitioner.Create(work, loadBalance: true).AsParallel().Select(item =>
            {
                var scan = new Scan(terms, only, partial, since);
                item(scan);
                return scan;
            });
            foreach (var scan in scans) scan.MergeInto(found);

            var byId = documents.GroupBy(pair => pair.Value.SessionId).ToDictionary(group => group.Key, group => group.First());
            // Naming a session with --session asks for it, even when it is the current one.
            var exclusions = Corpus.SearchExclusions(root, includeCurrent || only is not null);
            var sessions = found
                .Where(pair => pair.Value.Mentions.All(count => count > 0) && !Corpus.IsExcluded(pair.Key.SessionId, exclusions))
                .OrderByDescending(pair => pair.Value.LastAllTerms, StringComparer.Ordinal)
                .ThenByDescending(pair => pair.Value.LastMention, StringComparer.Ordinal)
                .ToList();
            var listed = sessions.Take(only is null ? top : 20).ToList();
            var after = since is { } time ? Utc(time) : null;
            if (json) WriteJson(root, terms, top, only, after, sessions.Count, listed, byId, stopwatch);
            else WriteText(terms, top, only, after, sessions.Count, listed, byId);
            return 0;
        }

        // Files are independent, so each one is a work item scanned on its own core; the Codex query goes first,
        // since it is the longest single item.
        private static List<Action<Scan>> WorkItems(string home, Dictionary<string, DocumentMeta> documents, bool outputs)
        {
            var vsCodeSessions = documents.Values.Where(document => document.Family == "vscode")
                .GroupBy(document => document.SourceRef, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().SessionId, StringComparer.OrdinalIgnoreCase);
            var work = new List<Action<Scan>>();
            // The Codex table is one 1 GB scan; row ranges let it run in parallel like the files.
            if (File.Exists(Parsers.CodexHistory(home)) && RowRange(Parsers.CodexHistory(home)) is var (first, last) && last >= first)
            {
                const int parts = 32;
                var size = (last - first) / parts + 1;
                for (var start = first; start <= last; start += size)
                {
                    var (from, to) = (start, Math.Min(last, start + size - 1));
                    work.Add(scan => ScanCodex(scan, Parsers.CodexHistory(home), from, to));
                }
            }
            work.AddRange(Parsers.Rglob(Path.Combine(home, ".claude", "projects"), "*.jsonl").Select(path => (Action<Scan>)(scan => ScanClaude(scan, path))));
            work.AddRange(Parsers.CopilotEventFiles(home).Select(path => (Action<Scan>)(scan => ScanCopilot(scan, path, outputs))));
            work.AddRange(Parsers.ChatFiles(Parsers.CodeUser(home)).Select(path => (Action<Scan>)(scan =>
                ScanVsCode(scan, path, vsCodeSessions.GetValueOrDefault(path) ?? Path.GetFileNameWithoutExtension(path)))));
            return work;
        }

        private static void WriteText(IReadOnlyList<string> terms, int top, string? only, string? since, int matching,
            List<KeyValuePair<(string Client, string SessionId), Found>> listed, Dictionary<string, KeyValuePair<string, DocumentMeta>> byId)
        {
            var window = since is null ? "" : $" since {since.Replace('T', ' ')} UTC";
            var text = new StringBuilder(only is null
                ? $"{matching} sessions contain all of: {string.Join(", ", terms)}{window}. Newest first; showing {listed.Count}.\n"
                : $"{matching} sessions starting with {only} contain all of: {string.Join(", ", terms)}{window}. Excerpts newest first, up to {top} each.\n");
            foreach (var ((client, sessionId), session) in listed)
            {
                var indexed = byId.TryGetValue(sessionId, out var document);
                var mentions = string.Join(", ", terms.Select((term, index) => $"{term} {session.Mentions[index]}"));
                var newest = session.LastAllTerms.Length > 0 ? $"all terms last {Compact.Time(session.LastAllTerms)}" : "";
                text.Append(Compact.SessionLine(sessionId, indexed ? document.Value.Client : client, newest, mentions, indexed ? document.Value.Title : "")).Append('\n');
                foreach (var snippet in Shown(session, only, top)) text.Append($"  [{Compact.Time(snippet.At)} {snippet.Where}] {snippet.Text}\n");
            }
            Compact.Write(Compact.Plain(text.ToString()));
        }

        private static void WriteJson(string root, IReadOnlyList<string> terms, int top, string? only, string? since, int matching,
            List<KeyValuePair<(string Client, string SessionId), Found>> listed, Dictionary<string, KeyValuePair<string, DocumentMeta>> byId, Stopwatch stopwatch)
        {
            var results = new JsonArray();
            foreach (var ((client, sessionId), session) in listed)
            {
                var indexed = byId.TryGetValue(sessionId, out var document);
                var mentions = new JsonObject();
                for (var index = 0; index < terms.Count; index++) mentions[terms[index]] = session.Mentions[index];
                results.Add(new JsonObject
                {
                    ["client"] = indexed ? document.Value.Client : client,
                    ["session_id"] = sessionId,
                    ["title"] = indexed ? document.Value.Title : "",
                    ["last_all_terms"] = session.LastAllTerms,
                    ["last_mention"] = session.LastMention,
                    ["mentions"] = mentions,
                    ["snippets"] = new JsonArray([
                        .. Shown(session, only, top).Select(JsonNode (snippet) => new JsonObject
                        {
                            ["at"] = snippet.At,
                            ["where"] = snippet.Where,
                            ["text"] = snippet.Text
                        })
                    ]),
                    ["transcript"] = indexed ? Path.Combine(root, "corpus", document.Key) : null
                });
            }
            var response = new JsonObject
            {
                ["terms"] = new JsonArray([.. terms.Select(term => (JsonNode)term)]),
                ["matching_sessions"] = matching,
                ["seconds"] = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
                ["sessions"] = results
            };
            if (since is not null) response["since"] = since;
            Console.Out.WriteLine(Indexer.Ascii(response.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            })));
        }

        private static IEnumerable<Snippet> Shown(Found session, string? only, int top) => only is null
            ? new[]
            {
                session.Best
            }.OfType<Snippet>()
            : session.All.DistinctBy(snippet => snippet.Text).OrderByDescending(snippet => snippet.At, StringComparer.Ordinal).Take(top);

        // Only what Codex was asked to do; command output stays out like other tool outputs.
        public static string CodexInput(string itemType, string itemJson)
        {
            using var json = Jsonl.Parse(itemJson);
            var item = json?.RootElement;
            return itemType switch
            {
                "commandExecution" => item.Get("command").Str(),
                "mcpToolCall" => $"{item.Get("server").Str()} {item.Get("tool").Str()} {item.Get("arguments").Str()}",
                "fileChange" => item.Get("changes").Str(),
                "webSearch" => item.Get("query").Str(),
                _ => itemJson
            };
        }

        public static string LikeEscape(string term) =>
            term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("'", "''");

        // The string value holding the most terms, cut to a window where they sit closest together, labelled by its JSON path.
        // A "name", "toolName" or "tool" property labels its object, so a path reads like "Write.input.content".
        public static Snippet? Excerpt(JsonElement element, string label, IReadOnlyList<string> terms)
        {
            Snippet? best = null;
            var bestCount = 0;
            Walk(element, label);
            return best;

            void Walk(JsonElement node, string path)
            {
                switch (node.ValueKind)
                {
                    case JsonValueKind.Object:
                        path = ObjectLabel(node, path);
                        foreach (var property in node.EnumerateObject()) Walk(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}");
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in node.EnumerateArray()) Walk(item, path);
                        break;
                    case JsonValueKind.String:
                        Consider(node.GetString()!, path);
                        break;
                    case JsonValueKind.Undefined:
                    case JsonValueKind.Number:
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                    case JsonValueKind.Null:
                    default:
                        break;
                }
            }

            void Consider(string value, string path)
            {
                var count = terms.Count(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
                if (count <= bestCount) return;
                bestCount = count;
                var (text, score) = Window(value, terms);
                best = new Snippet("", path, text, score);
            }
        }

        // When several of these string properties are present, the last one wins.
        private static string ObjectLabel(JsonElement node, string path)
        {
            foreach (var key in new[]
                     {
                         "name", "toolName", "tool"
                     })
            {
                if (node.TryGetProperty(key, out var name) && name.ValueKind == JsonValueKind.String) path = name.GetString()!;
            }
            return path;
        }

        // Centred on the tightest cluster of terms around an occurrence of the rarest term, which says more than a common
        // one such as "threat". The score is how many terms the window shows.
        private static (string Text, int Score) Window(string value, IReadOnlyList<string> terms)
        {
            var present = terms.Where(term => value.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
            var center = 0;
            if (present.Count > 0)
            {
                var rarest = present.MinBy(term => Occurrences(value, term).Take(1000).Count())!;
                center = Occurrences(value, rarest).Take(20).Select(at => Cluster(value, at, rarest, present)).MaxBy(cluster => cluster.Score).Center;
            }
            var start = Math.Clamp(center - WindowChars / 2, 0, Math.Max(0, value.Length - WindowChars));
            var end = Math.Min(value.Length, start + WindowChars);
            var shown = value[start..end];
            var text = (start > 0 ? "..." : "") + Whitespace().Replace(shown, " ").Trim() + (end < value.Length ? "..." : "");
            return (text, present.Count(term => shown.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        private static IEnumerable<int> Occurrences(string value, string term)
        {
            for (var at = value.IndexOf(term, StringComparison.OrdinalIgnoreCase); at >= 0; at = value.IndexOf(term, at + term.Length, StringComparison.OrdinalIgnoreCase))
                yield return at;
        }

        // Adds the nearest occurrence of each other term while the span still fits in one window.
        private static (int Score, int Center) Cluster(string value, int at, string rarest, IReadOnlyList<string> present)
        {
            var (low, high, score) = (at, at + rarest.Length, 1);
            foreach (var term in present.Where(term => term != rarest))
            {
                var before = at > 0 ? value.LastIndexOf(term, at, StringComparison.OrdinalIgnoreCase) : -1;
                var after = value.IndexOf(term, at, StringComparison.OrdinalIgnoreCase);
                var nearest = before < 0
                    ? after
                    : after < 0
                        ? before
                        : at - before <= after - at
                            ? before
                            : after;
                if (nearest < 0 || Math.Max(high, nearest + term.Length) - Math.Min(low, nearest) > WindowChars - 20) continue;
                (low, high, score) = (Math.Min(low, nearest), Math.Max(high, nearest + term.Length), score + 1);
            }
            return (score, (low + high) / 2);
        }

        private static string Utc(string timestamp) =>
            DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                ? Utc(time)
                : "";

        private static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        private static IEnumerable<string> Lines(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line) yield return line;
        }

        sealed internal class Found
        {
            public Found(int terms)
            {
                Mentions = new int[terms];
            }

            public int[] Mentions { get; }

            public string LastMention { get; set; } = "";

            public string LastAllTerms { get; set; } = "";

            public Snippet? Best { get; private set; }

            // Every matching excerpt, collected only when one session is being drilled into.
            public List<Snippet> All { get; } = [];

            // The excerpt showing the most terms wins, then the newest.
            public void Offer(Snippet snippet)
            {
                if (Best is null || snippet.Score > Best.Score || snippet.Score == Best.Score && string.CompareOrdinal(snippet.At, Best.At) > 0) Best = snippet;
            }

            public void Merge(Found other)
            {
                for (var index = 0; index < Mentions.Length; index++) Mentions[index] += other.Mentions[index];
                if (string.CompareOrdinal(other.LastMention, LastMention) > 0) LastMention = other.LastMention;
                if (string.CompareOrdinal(other.LastAllTerms, LastAllTerms) > 0) LastAllTerms = other.LastAllTerms;
                if (other.Best is { } best) Offer(best);
                All.AddRange(other.All);
            }
        }

        // One file's results, merged into the shared set on the calling thread.
        sealed internal class Scan
        {
            private readonly Dictionary<(string Client, string SessionId), Found> _found = [];
            private readonly string[][] _forms;
            private readonly string? _only;
            private readonly bool _partial;
            private readonly string? _since;

            public Scan(IReadOnlyList<string> terms, string? only = null, bool partial = false, DateTimeOffset? since = null)
            {
                Terms = terms;
                _forms = [.. terms.Select(Forms)];
                _only = only;
                _partial = partial;
                _since = since is { } time ? Utc(time) : null;
            }

            public IReadOnlyList<string> Terms { get; }

            // Raw JSON escapes backslashes and quotes, so a term such as a Windows path is also looked for in that form.
            public static string[] Forms(string term) =>
            [
                .. new[]
                {
                    term, term.Replace(@"\", @"\\").Replace("\"", "\\\"")
                }.Distinct()
            ];

            // A cheap substring prefilter; Note applies whole-word matching and self-invocation filtering.
            public bool Mentions(string text) => _forms.Any(term => Has(text, term, true));

            private static bool Has(string text, string[] term, bool partial) =>
                term.Any(form => partial ? text.Contains(form, StringComparison.OrdinalIgnoreCase) : HasWord(text, form));

            // An occurrence counts only if an alphanumeric edge of the term is not continued by a word character.
            public static bool HasWord(string text, string term)
            {
                if (term.Length == 0) return false;
                var checkStart = char.IsLetterOrDigit(term[0]);
                var checkEnd = char.IsLetterOrDigit(term[^1]);
                for (var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                     at >= 0;
                     at = at + 1 < text.Length ? text.IndexOf(term, at + 1, StringComparison.OrdinalIgnoreCase) : -1)
                {
                    var end = at + term.Length;
                    if (checkStart && at > 0 && IsWordChar(text[at - 1]) && !AfterEscape(text, at - 1)) continue;
                    if (checkEnd && end < text.Length && IsWordChar(text[end])) continue;
                    return true;
                }
                return false;
            }

            private static bool IsWordChar(char value) => char.IsLetterOrDigit(value) || value == '_';

            // In raw JSON, "\nRAM" is a line break before the word, not the letter n; an odd run of backslashes marks an escape.
            private static bool AfterEscape(string text, int at)
            {
                if ("nrtbf".IndexOf(text[at]) < 0) return false;
                var slashes = 0;
                for (var index = at - 1; index >= 0 && text[index] == '\\'; index--) slashes++;
                return slashes % 2 == 1;
            }

            // The memory tool's own commands mention every term searched for, so they would make the searching session rank first.
            // Besides a direct run, it may be run from a variable (& $exe find) or by dotnet run after "--"; those count only
            // beside a mention of one of its files, so prose about the tool does not.
            public static bool IsSelfInvocation(string text) =>
                text.Contains("SessionMemory", StringComparison.OrdinalIgnoreCase)
                && (SelfInvocation().IsMatch(text) || ToolFile().IsMatch(text) && IndirectInvocation().IsMatch(text));

            public bool Matches(string text) => !IsSelfInvocation(text) && _forms.All(term => Has(text, term, _partial));

            // The snippet is built only for the newest records holding every term, since walking JSON costs more than matching.
            public void Note(string client, string sessionId, string text, string timestamp, Func<Snippet?> snippet)
            {
                if (_only is not null && !sessionId.StartsWith(_only, StringComparison.OrdinalIgnoreCase)) return;
                // Timestamps share one UTC format, so they compare as text; one that could not be parsed is empty and comes first.
                if (_since is not null && string.CompareOrdinal(timestamp, _since) < 0) return;
                if (IsSelfInvocation(text)) return;
                var present = _forms.Select(term => Has(text, term, _partial)).ToArray();
                if (!present.Any(hit => hit)) return;
                var session = Tally(client, sessionId, timestamp, present);
                if (!present.All(hit => hit)) return;
                if (string.CompareOrdinal(timestamp, session.LastAllTerms) > 0) session.LastAllTerms = timestamp;
                KeepSnippet(session, timestamp, snippet);
            }

            private Found Tally(string client, string sessionId, string timestamp, bool[] present)
            {
                if (!_found.TryGetValue((client, sessionId), out var session)) _found[(client, sessionId)] = session = new Found(Terms.Count);
                for (var index = 0; index < present.Length; index++) session.Mentions[index] += present[index] ? 1 : 0;
                if (string.CompareOrdinal(timestamp, session.LastMention) > 0) session.LastMention = timestamp;
                return session;
            }

            private void KeepSnippet(Found session, string timestamp, Func<Snippet?> snippet)
            {
                if (_only is not null)
                {
                    if (session.All.Count < 500 && snippet() is { } each)
                        session.All.Add(each with
                        {
                            At = timestamp
                        });
                    return;
                }
                if (session.Best is { } best && best.Score == Terms.Count && string.CompareOrdinal(timestamp, best.At) <= 0) return;
                if (snippet() is { } excerpt)
                    session.Offer(excerpt with
                    {
                        At = timestamp
                    });
            }

            public IReadOnlyDictionary<(string Client, string SessionId), Found> Sessions => _found;

            public void MergeInto(Dictionary<(string Client, string SessionId), Found> target)
            {
                foreach (var (key, session) in _found)
                {
                    if (target.TryGetValue(key, out var existing)) existing.Merge(session);
                    else target[key] = session;
                }
            }
        }

        public sealed record Snippet(string At, string Where, string Text, int Score);

        private static void ScanClaude(Scan scan, string path)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var subagent = path.Split(Path.DirectorySeparatorChar).Any(part => part.Equals("subagents", StringComparison.OrdinalIgnoreCase));
            foreach (var line in Lines(path))
            {
                if (!scan.Mentions(line) || line.Contains("\"tool_result\"", StringComparison.Ordinal)) continue;
                using var json = Jsonl.Parse(line);
                if (json is null) continue;
                var record = (JsonElement?)json.RootElement;
                var sessionId = record.Get("sessionId").StrOr(stem);
                scan.Note("claude", subagent ? $"{sessionId}:subagent:{stem}" : sessionId, line, Utc(record.Get("timestamp").StrOr("")),
                    () => ClaudeExcerpt(line, scan.Terms));
            }
        }

        // Parses its own document, so the deferred excerpt never depends on one disposed by the scanning loop.
        private static Snippet? ClaudeExcerpt(string line, IReadOnlyList<string> terms)
        {
            using var json = Jsonl.Parse(line);
            return json is null ? null : Excerpt(json.RootElement.TryGetProperty("message", out var message) ? message : json.RootElement, "", terms);
        }

        private static void ScanCopilot(Scan scan, string path, bool outputs)
        {
            var sessionId = Path.GetFileName(Path.GetDirectoryName(path)!);
            var selfCalls = outputs ? new HashSet<string>(StringComparer.Ordinal) : null;
            foreach (var line in Lines(path))
            {
                if (selfCalls is not null) RememberSelfCall(line, selfCalls);
                if (scan.Mentions(line)) NoteCopilot(scan, sessionId, line, selfCalls);
            }
        }

        // Tool results count only when selfCalls is given, which is when they are asked for.
        private static void NoteCopilot(Scan scan, string sessionId, string line, HashSet<string>? selfCalls)
        {
            using var json = Jsonl.Parse(line);
            var record = json?.RootElement;
            var type = record.Get("type").AsString();
            if (type == CopilotOutput && selfCalls is not null) NoteCopilotOutput(scan, sessionId, record, selfCalls);
            else if (CopilotTypes.Contains(type))
                scan.Note("copilot", sessionId, line, Utc(record.Get("timestamp").StrOr("")),
                    () => record.Get("data") is { } data ? Excerpt(data, record.Get("type").Str(), scan.Terms) : null);
        }

        // Results of the memory tool's own runs repeat what it found in other sessions, so the calls that started them are remembered.
        private static void RememberSelfCall(string line, HashSet<string> selfCalls)
        {
            if (!line.Contains("\"tool.execution_start\"", StringComparison.Ordinal) || !Scan.IsSelfInvocation(line)) return;
            using var json = Jsonl.Parse(line);
            var record = json?.RootElement;
            if (record.Get("type").AsString() == "tool.execution_start" && record.Get("data").Get("toolCallId").AsString() is { } id) selfCalls.Add(id);
        }

        // A result is matched on its content or error alone, since its telemetry repeats the call's arguments.
        private static void NoteCopilotOutput(Scan scan, string sessionId, JsonElement? record, HashSet<string> selfCalls)
        {
            var data = record.Get("data");
            if (data.Get("toolCallId").AsString() is { } id && selfCalls.Contains(id)) return;
            var result = data.Get("result");
            if ((result ?? data.Get("error")) is not { } output) return;
            var label = result is null ? "tool error" : "tool output";
            scan.Note("copilot", sessionId, output.GetRawText(), Utc(record.Get("timestamp").StrOr("")), () => OutputExcerpt(output, label, scan.Terms));
        }

        // A result carries no tool name, and a nested "name" would relabel its path, so the label goes in front.
        private static Snippet? OutputExcerpt(JsonElement output, string label, IReadOnlyList<string> terms) =>
            Excerpt(output, "", terms) is { } excerpt
                ? excerpt with
                {
                    Where = $"{label} {excerpt.Where}".TrimEnd()
                }
                : null;

        private static (long First, long Last) RowRange(string history)
        {
            using var database = new SqliteReader(history);
            var row = database.Query("SELECT min(rowid), max(rowid) FROM thread_items").First();
            return (Sql.IntOr(row[0], 1), Sql.IntOr(row[1], 0));
        }

        private static void ScanCodex(Scan scan, string history, long from, long to)
        {
            using var database = new SqliteReader(history);
            var filter = string.Join(" OR ", scan.Terms.SelectMany(Scan.Forms).Select(term => $"item_json LIKE '%{LikeEscape(term)}%' ESCAPE '\\'"));
            var query = $"SELECT thread_id, item_type, created_at_ms, item_json FROM thread_items WHERE rowid BETWEEN {from} AND {to} AND item_type IN " +
                        $"('userMessage','agentMessage','commandExecution','mcpToolCall','fileChange','webSearch') AND ({filter})";
            foreach (var row in database.Query(query))
            {
                var itemType = Sql.Str(row[1]);
                var itemJson = Sql.Str(row[3]);
                scan.Note("codex", Sql.Str(row[0]), CodexInput(itemType, itemJson), Utc(Transcript.IsoFromMs(Sql.Number(row[2]))), () =>
                {
                    using var json = Jsonl.Parse(itemJson);
                    if (json is null) return null;
                    var item = json.RootElement;
                    var input = itemType switch
                    {
                        "commandExecution" => item.TryGetProperty("command", out var command) ? command : (JsonElement?)null,
                        "mcpToolCall" => item.TryGetProperty("arguments", out var arguments) ? arguments : null,
                        "fileChange" => item.TryGetProperty("changes", out var changes) ? changes : null,
                        "webSearch" => item.TryGetProperty("query", out var search) ? search : null,
                        _ => item
                    };
                    var label = itemType == "mcpToolCall" ? $"{((JsonElement?)item).Get("tool").Str()}" : itemType;
                    return input is { } element ? Excerpt(element, label, scan.Terms) : null;
                });
            }
        }

        private static void ScanVsCode(Scan scan, string path, string sessionId)
        {
            var modified = File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            foreach (var line in Lines(path))
            {
                if (!scan.Mentions(line)) continue;
                var match = VsCodeTimestamp.Match(line);
                scan.Note("vscode-copilot", sessionId, line,
                    match.Success ? Utc(Transcript.IsoFromMs(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))) : modified,
                    () =>
                    {
                        using var json = Jsonl.Parse(line);
                        return json is null ? null : Excerpt(json.RootElement, "", scan.Terms);
                    });
            }
        }

        [GeneratedRegex("\"timestamp\":(\\d{13})", RegexOptions.Compiled)]
        private static partial Regex MyRegex();

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();

        // Raw JSON logs escape the quote after the executable, hence the optional backslash.
        [GeneratedRegex("(?i)Search-SessionMemory\\.ps1|SessionMemory(\\.exe|\\.dll)?\\\\?[\"']?\\s+(--root\\s+\\S+\\s+)?(search|find|show)\\b")]
        private static partial Regex SelfInvocation();

        [GeneratedRegex(@"(?i)SessionMemory\.(exe|dll|csproj)\b")]
        private static partial Regex ToolFile();

        // A command after a variable holding the executable (& $exe find, "$exe" find) or after the "--" that ends dotnet run's own options.
        [GeneratedRegex(@"(?i)(\$\{?\w+\}?\\?[""']?|(?<=\s)--)\s+(--root\s+\S+\s+)?(search|find|show)\s")]
        private static partial Regex IndirectInvocation();
    }
}
