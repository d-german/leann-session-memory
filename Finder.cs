using System.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using SessionMemory.Clients;

namespace SessionMemory
{
    /// <summary>
    /// What to find. Only names a session ID or prefix whose excerpts are all listed; Since keeps records at or after that time;
    /// Outputs adds Copilot CLI tool results to what is searched; Clients are the clients whose stores are searched.
    /// </summary>
    sealed internal record FindRequest
    {
        public required string Root { get; init; }

        public required string Home { get; init; }

        public required ImmutableArray<string> Terms { get; init; }

        public int Top { get; init; } = 8;

        public bool Json { get; init; }

        public string? Only { get; init; }

        public bool Partial { get; init; }

        public bool IncludeCurrent { get; init; }

        public DateTimeOffset? Since { get; init; }

        public bool Outputs { get; init; }

        // Every registered client unless the caller hands in others, as the self-test does.
        public ImmutableArray<ISessionClient> Clients { get; init; } = ClientRegistry.All;
    }

    /// <summary>
    /// Exact-term search over the raw stores. Unlike the semantic index it also covers tool inputs such as edits,
    /// commands and page updates, whose full text would nearly triple the index. Tool outputs stay out as noise unless asked for.
    /// </summary>
    internal static partial class Finder
    {
        private const int WindowChars = 300;

        // With a session ID (or prefix), lists every matching excerpt in that session instead of one per session.
        public static int Run(FindRequest request)
        {
            var stopwatch = Stopwatch.StartNew();
            // find --top 3 names no terms; a request built without any behaves the same.
            var terms = request.Terms.IsDefaultOrEmpty ? [] : request.Terms;
            var manifest = Store.Load<Manifest>(Path.Combine(request.Root, "state", "manifest.json"));
            var documents = manifest?.Documents ?? [];
            var work = WorkItems(request, documents);
            // Load balancing hands files out one at a time, so a few large logs don't pile up on one core.
            var found = new Dictionary<SessionKey, Found>();
            var scans = Partitioner.Create(work, loadBalance: true).AsParallel().Select(item =>
            {
                var scan = new Scan(terms, request.Only, request.Partial, request.Since);
                item(scan);
                return scan;
            });
            foreach (var scan in scans) scan.MergeInto(found);

            var byId = documents.GroupBy(pair => pair.Value.SessionId).ToDictionary(group => group.Key, group => group.First());
            // Naming a session with --session asks for it, even when it is the current one.
            var exclusions = Corpus.SearchExclusions(request.Root, request.IncludeCurrent || request.Only is not null);
            var sessions = found
                .Where(pair => pair.Value.Mentions.All(count => count > 0) && !Corpus.IsExcluded(pair.Key.SessionId, exclusions))
                .OrderByDescending(pair => pair.Value.LastAllTerms, StringComparer.Ordinal)
                .ThenByDescending(pair => pair.Value.LastMention, StringComparer.Ordinal)
                .ToList();
            var listed = sessions.Take(request.Only is null ? request.Top : 20).ToList();
            var after = request.Since is { } time ? Utc(time) : null;
            if (request.Json) WriteJson(request, terms, after, sessions.Count, listed, byId, stopwatch);
            else WriteText(request, terms, after, sessions.Count, listed, byId);
            return 0;
        }

        // Files are independent, so each one is a work item scanned on its own core. Every client hands out its own work, in
        // registry order, so the Codex row ranges, the longest single items, go first.
        private static List<Action<Scan>> WorkItems(FindRequest request, Dictionary<string, DocumentMeta> documents)
        {
            var context = new FindContext(request.Home, documents, request.Outputs);
            return [.. request.Clients.SelectMany(client => client.FindWork(context))];
        }

        private static void WriteText(FindRequest request, ImmutableArray<string> terms, string? since, int matching,
            List<KeyValuePair<SessionKey, Found>> listed, Dictionary<string, KeyValuePair<string, DocumentMeta>> byId)
        {
            var window = since is null ? "" : $" since {since.Replace('T', ' ')} UTC";
            var text = new StringBuilder(request.Only is null
                ? $"{matching} sessions contain all of: {string.Join(", ", terms)}{window}. Newest first; showing {listed.Count}.\n"
                : $"{matching} sessions starting with {request.Only} contain all of: {string.Join(", ", terms)}{window}. Excerpts newest first, up to {request.Top} each.\n");
            foreach (var (key, session) in listed)
            {
                var document = byId.TryFind(key.SessionId);
                var mentions = string.Join(", ", terms.Select((term, index) => $"{term} {session.Mentions[index]}"));
                var newest = session.LastAllTerms.Length > 0 ? $"all terms last {Compact.Time(session.LastAllTerms)}" : "";
                text.Append(Compact.SessionLine(key.SessionId, document.Match((_, meta) => meta.Client, () => key.Client), newest, mentions,
                    document.Match((_, meta) => meta.Title, () => ""))).Append('\n');
                foreach (var snippet in Shown(session, request.Only, request.Top)) text.Append($"  [{Compact.Time(snippet.At)} {snippet.Where}] {snippet.Text}\n");
            }
            Compact.Write(Compact.Plain(text.ToString()));
        }

        private static void WriteJson(FindRequest request, ImmutableArray<string> terms, string? since, int matching,
            List<KeyValuePair<SessionKey, Found>> listed, Dictionary<string, KeyValuePair<string, DocumentMeta>> byId, Stopwatch stopwatch)
        {
            var results = new JsonArray();
            foreach (var (key, session) in listed)
            {
                var document = byId.TryFind(key.SessionId);
                var mentions = new JsonObject();
                for (var index = 0; index < terms.Length; index++) mentions[terms[index]] = session.Mentions[index];
                results.Add(new JsonObject
                {
                    ["client"] = document.Match((_, meta) => meta.Client, () => key.Client),
                    ["session_id"] = key.SessionId,
                    ["title"] = document.Match((_, meta) => meta.Title, () => ""),
                    ["last_all_terms"] = session.LastAllTerms,
                    ["last_mention"] = session.LastMention,
                    ["mentions"] = mentions,
                    ["snippets"] = new JsonArray([
                        .. Shown(session, request.Only, request.Top).Select(JsonNode (snippet) => new JsonObject
                        {
                            ["at"] = snippet.At,
                            ["where"] = snippet.Where,
                            ["text"] = snippet.Text
                        })
                    ]),
                    ["transcript"] = document.Map(pair => Path.Combine(request.Root, "corpus", pair.Key)).GetValueOrDefault()
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

        public static string LikeEscape(string term) =>
            term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("'", "''");

        // The string value holding the most terms, cut to a window where they sit closest together, labelled by its JSON path.
        // A "name", "toolName" or "tool" property labels its object, so a path reads like "Write.input.content".
        public static Snippet? Excerpt(JsonElement element, string label, ImmutableArray<string> terms)
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
                var count = CountContained(value, terms);
                if (count <= bestCount) return;
                bestCount = count;
                var (text, score) = Window(value, terms);
                best = new Snippet("", path, text, score);
            }
        }

        // How many of the terms occur anywhere in the value, ignoring case.
        private static int CountContained(string value, ImmutableArray<string> terms)
        {
            var count = 0;
            foreach (var term in terms.AsSpan())
            {
                if (value.Contains(term, StringComparison.OrdinalIgnoreCase)) count++;
            }
            return count;
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
        private static (string Text, int Score) Window(string value, ImmutableArray<string> terms)
        {
            ImmutableArray<string> present = [.. terms.Where(term => value.Contains(term, StringComparison.OrdinalIgnoreCase))];
            var center = 0;
            if (present.Length > 0)
            {
                var rarest = present.MinBy(term => Occurrences(value, term).Take(1000).Count())!;
                center = Occurrences(value, rarest).Take(20).Select(at => Cluster(value, at, rarest, present)).MaxBy(cluster => cluster.Score).Center;
            }
            var start = Math.Clamp(center - WindowChars / 2, 0, Math.Max(0, value.Length - WindowChars));
            var end = Math.Min(value.Length, start + WindowChars);
            var shown = value[start..end];
            var text = (start > 0 ? "..." : "") + Whitespace().Replace(shown, " ").Trim() + (end < value.Length ? "..." : "");
            return (text, CountContained(shown, present));
        }

        private static IEnumerable<int> Occurrences(string value, string term)
        {
            for (var at = value.IndexOf(term, StringComparison.OrdinalIgnoreCase); at >= 0; at = value.IndexOf(term, at + term.Length, StringComparison.OrdinalIgnoreCase))
                yield return at;
        }

        // Adds the nearest occurrence of each other term while the span still fits in one window.
        private static (int Score, int Center) Cluster(string value, int at, string rarest, ImmutableArray<string> present)
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

        public static string Utc(string timestamp) =>
            DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                ? Utc(time)
                : "";

        private static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        public static IEnumerable<string> Lines(string path)
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
            private readonly Dictionary<SessionKey, Found> _found = [];
            private readonly ImmutableArray<ImmutableArray<string>> _forms;
            // Which terms the record being noted holds, reused so that noting a record allocates nothing.
            private readonly bool[] _present;
            private readonly string? _only;
            private readonly bool _partial;
            private readonly string? _since;

            public Scan(ImmutableArray<string> terms, string? only = null, bool partial = false, DateTimeOffset? since = null)
            {
                Terms = terms.IsDefault ? [] : terms;
                _forms = [.. Terms.Select(Forms)];
                _present = new bool[Terms.Length];
                _only = only;
                _partial = partial;
                _since = since is { } time ? Utc(time) : null;
            }

            public ImmutableArray<string> Terms { get; }

            // Raw JSON escapes backslashes and quotes, so a term such as a Windows path is also looked for in that form.
            public static ImmutableArray<string> Forms(string term) =>
            [
                .. new[]
                {
                    term, term.Replace(@"\", @"\\").Replace("\"", "\\\"")
                }.Distinct()
            ];

            // A cheap substring prefilter; Note applies whole-word matching and self-invocation filtering.
            public bool Mentions(string text)
            {
                foreach (var term in _forms.AsSpan())
                {
                    if (Has(text, term, true)) return true;
                }
                return false;
            }

            private static bool Has(string text, ImmutableArray<string> term, bool partial)
            {
                foreach (var form in term.AsSpan())
                {
                    if (partial ? text.Contains(form, StringComparison.OrdinalIgnoreCase) : HasWord(text, form)) return true;
                }
                return false;
            }

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

            public bool Matches(string text) => !IsSelfInvocation(text) && HasAll(text);

            private bool HasAll(string text)
            {
                foreach (var term in _forms.AsSpan())
                {
                    if (!Has(text, term, _partial)) return false;
                }
                return true;
            }

            // The snippet is built only for the newest records holding every term, since walking JSON costs more than matching.
            public void Note(string client, string sessionId, string text, string timestamp, Func<Snippet?> snippet)
            {
                if (_only is not null && !sessionId.StartsWith(_only, StringComparison.OrdinalIgnoreCase)) return;
                // Timestamps share one UTC format, so they compare as text; one that could not be parsed is empty and comes first.
                if (_since is not null && string.CompareOrdinal(timestamp, _since) < 0) return;
                if (IsSelfInvocation(text)) return;
                var (any, all) = Presence(text);
                if (!any) return;
                var session = Tally(client, sessionId, timestamp);
                if (!all) return;
                if (string.CompareOrdinal(timestamp, session.LastAllTerms) > 0) session.LastAllTerms = timestamp;
                KeepSnippet(session, timestamp, snippet);
            }

            // Fills _present and tells whether the text holds any and all of the terms.
            private (bool Any, bool All) Presence(string text)
            {
                var any = false;
                var all = true;
                for (var index = 0; index < _forms.Length; index++)
                {
                    _present[index] = Has(text, _forms[index], _partial);
                    any |= _present[index];
                    all &= _present[index];
                }
                return (any, all);
            }

            private Found Tally(string client, string sessionId, string timestamp)
            {
                var key = new SessionKey(client, sessionId);
                if (!_found.TryGetValue(key, out var session)) _found[key] = session = new Found(Terms.Length);
                for (var index = 0; index < _present.Length; index++) session.Mentions[index] += _present[index] ? 1 : 0;
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
                if (session.Best is { } best && best.Score == Terms.Length && string.CompareOrdinal(timestamp, best.At) <= 0) return;
                if (snippet() is { } excerpt)
                    session.Offer(excerpt with
                    {
                        At = timestamp
                    });
            }

            public IReadOnlyDictionary<SessionKey, Found> Sessions => _found;

            public void MergeInto(Dictionary<SessionKey, Found> target)
            {
                foreach (var (key, session) in _found)
                {
                    if (target.TryGetValue(key, out var existing)) existing.Merge(session);
                    else target[key] = session;
                }
            }
        }

        // A session as one client names it; it keys the results, so it compares by value.
        internal readonly record struct SessionKey
        {
            public SessionKey(string client, string sessionId)
            {
                Client = client;
                SessionId = sessionId;
            }

            public string Client { get; init; }

            public string SessionId { get; init; }
        }

        public sealed record Snippet(string At, string Where, string Text, int Score);

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
