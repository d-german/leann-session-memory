using System.Text.Json.Nodes;
using SessionMemory.Clients;

namespace SessionMemory
{
    internal static partial class SelfTest
    {
        private static void NavigationChecks()
        {
            var temporary = Directory.CreateTempSubdirectory("navigation-test-");
            using var cleanup = new TemporaryDirectory(temporary.FullName);
            var root = temporary.FullName;
            var corpus = Directory.CreateDirectory(Path.Combine(root, "corpus")).FullName;
            Directory.CreateDirectory(Path.Combine(root, "state"));
            var paths = new ClientContext(root, root);
            var exclusions = new ExclusionService(paths, new CurrentSession([]));
            var history = new HistoryNavigation(paths, exclusions);
            var docs = new Dictionary<string, DocumentMeta>();
            const string alpha = "11111111-1111-4111-8111-111111111111";
            const string beta = "22222222-2222-4222-8222-222222222222";
            const string hidden = "33333333-3333-4333-8333-333333333333";
            string Message(int number, string date, string text) =>
                $"## Message {number:000000} part 1/1\nrole: user\ntimestamp: {date}\ntext:\n{text}\n\n";
            void Add(string file, string id, string body)
            {
                File.WriteAllText(Path.Combine(corpus, file), body);
                docs[file] = new DocumentMeta { SessionId = id, Client = "test", Title = "sample", Sha256 = Transcript.Sha256(body) };
            }
            var a = Message(1, "2026-01-01T00:00:00+00:00", $"Trace Café [link](https://example.test/a/pull/42) {beta}")
                    + Message(2, "2026-01-01T00:01:00+00:00", "trace cafe\u0301 later")
                    + Message(3, "not-a-date", "TRACE unknown");
            Add("a.md", alpha, a);
            Add("b.md", beta, Message(1, "2026-01-01T03:00:00+02:00", "TRACE https://example.test/a/pull/42")
                              + Message(2, "2025-01-01T00:00:00Z", "https://example.test/b/pull/42")
                              + Message(3, "2025-01-01T00:01:00Z", "https://example.test/a/pull/42?focus=1"));
            Add("hidden.md", hidden, Message(1, "2028-01-01T00:00:00Z", "TRACE https://example.test/a/pull/42 secret"));
            Store.Save(Path.Combine(root, "state", "manifest.json"), new Manifest { Documents = docs });
            Store.Save(exclusions.FilePath, new[] { hidden });

            JsonObject Read(string value) => JsonNode.Parse(value)!.AsObject();
            var first = Read(history.Timeline(new TimelineRequest(["trace"], 1, Json: true)).Value);
            Check(first["total_matches"]!.GetValue<long>() == 4 && first["undated"]!.GetValue<long>() == 1,
                "timeline deduplicates overlapping messages, folds case and excludes hidden sessions");
            Check(Read(history.Timeline(new TimelineRequest(["Café"], Json: true)).Value)["total_matches"]!.GetValue<long>() == 1
                  && Read(history.Timeline(new TimelineRequest(["cafe\u0301"], Json: true)).Value)["total_matches"]!.GetValue<long>() == 1,
                "timeline keeps composed and decomposed literal text distinct");
            Check(first["items"]![0]!["session_id"]!.GetValue<string>() == beta
                  && first["items"]![0]!["timestamp_utc"]!.GetValue<string>().StartsWith("2026-01-01T01:00:00", StringComparison.Ordinal),
                "timeline orders UTC instants across offsets");
            var cursor = first["next_cursor"]!.GetValue<string>();
            var second = Read(history.Timeline(new TimelineRequest(["trace"], 1, cursor, Json: true)).Value);
            Check(second["items"]![0]!["session_id"]!.GetValue<string>() == alpha && second["items"]![0]!["message"]!.GetValue<long>() == 2,
                "timeline cursor continues without repeating a message");
            Check(history.Timeline(new TimelineRequest(["different"], 1, cursor, Json: true)).IsFailure
                  && history.Timeline(new TimelineRequest(["trace"], 1, "not-base64", Json: true)).IsFailure,
                "timeline rejects malformed and mismatched cursors");
            var related = Read(history.Related(new RelatedRequest(alpha, 1, Json: true)).Value);
            Check(related["total_uri_messages"]!.GetValue<long>() == 1 && related["total_sessions"]!.GetValue<int>() == 1
                  && !related.ToJsonString().Contains(hidden, StringComparison.Ordinal),
                "related finds exact URI and visible session references without aliases or excluded messages");
            Check(history.Related(new RelatedRequest(hidden, 1, Json: true)).IsFailure,
                "related cannot read an excluded source");
            Check(history.Related(new RelatedRequest("../a.md", 1, Json: true)).IsFailure,
                "related treats traversal as an unavailable session ID");
            const string targetId = "source-12345678";
            const string boundaryId = "boundary-12345678";
            Add("a.md", alpha, Message(1, "2026-01-01T00:00:00Z",
                $"References {targetId.ToUpperInvariant()} and {boundaryId}x and {hidden}."));
            Store.Save(Path.Combine(root, "state", "manifest.json"), new Manifest { Documents = docs });
            Check(Read(history.Related(new RelatedRequest(alpha, 1, Json: true)).Value)["total_sessions"]!.GetValue<int>() == 0,
                "related excludes hidden targets before a new target is indexed");
            Add("target.md", targetId, Message(1, "2026-01-01T00:00:00Z", "target"));
            Add("boundary.md", boundaryId, Message(1, "2026-01-01T00:00:00Z", "boundary"));
            Store.Save(Path.Combine(root, "state", "manifest.json"), new Manifest { Documents = docs });
            var newTarget = Read(history.Related(new RelatedRequest(alpha, 1, Json: true)).Value);
            Check(newTarget["total_sessions"]!.GetValue<int>() == 1
                  && newTarget["items"]![0]!["session_id"]!.GetValue<string>() == targetId
                  && newTarget["source"]!["excerpt"]!.GetValue<string>().Contains(targetId, StringComparison.OrdinalIgnoreCase),
                "related finds newly known non-UUID IDs without matching ID prefixes or excluded sessions");
            var longPrefix = "😀\n\n" + string.Concat(Enumerable.Repeat("ordinary context ", 450));
            var lateSource = longPrefix + $"Needle [link](https://EXAMPLE.test/a/pull/42) {alpha} {beta}";
            var lateTarget = longPrefix + "https://example.test/a/pull/42 context";
            Add("a.md", alpha, Message(1, "2026-01-01T00:00:00Z", lateSource));
            Add("b.md", beta, Message(1, "2026-01-01T01:00:00Z", lateTarget));
            Store.Save(Path.Combine(root, "state", "manifest.json"), new Manifest { Documents = docs });
            var lateTimeline = Read(history.Timeline(new TimelineRequest(["absent", "needle"], Json: true)).Value);
            var lateHit = lateTimeline["items"]![0]!;
            Check(lateHit["excerpt"]!.GetValue<string>().Contains("Needle", StringComparison.Ordinal)
                  && lateHit["excerpt"]!.GetValue<string>().Contains("[earlier text omitted]", StringComparison.Ordinal)
                  && lateHit["read_hint"]!.GetValue<string>().Contains($"--offset {longPrefix.Length - 200}", StringComparison.Ordinal),
                "timeline centers late OR matches and points to their original UTF-16 offset");
            var original = new SessionReader(paths, exclusions).Show(new ShowRequest
            {
                Id = alpha, MessageNumber = 1, Offset = longPrefix.Length - 200, Chars = 1000
            });
            Check(original.IsSuccess && original.Value.Contains("Needle", StringComparison.Ordinal),
                "navigation offset reads the same original message despite blank lines and surrogate pairs");
            var lateRelated = Read(history.Related(new RelatedRequest(alpha, 1, Json: true)).Value);
            var sourceCitation = lateRelated["source"]!;
            var targetCitation = lateRelated["items"]!.AsArray().First(item => item!["kind"]!.GetValue<string>() == "uri")!;
            Check(sourceCitation["excerpt"]!.GetValue<string>().Contains("https://EXAMPLE.test/a/pull/42", StringComparison.Ordinal)
                  && targetCitation["excerpt"]!.GetValue<string>().Contains("https://example.test/a/pull/42", StringComparison.Ordinal)
                  && sourceCitation["read_hint"]!.GetValue<string>().Contains("--offset ", StringComparison.Ordinal)
                  && targetCitation["read_hint"]!.GetValue<string>().Contains($"--offset {longPrefix.Length - 200}", StringComparison.Ordinal),
                "related centers the original URI spelling in source and target with UTF-16 offsets");
            Check(lateRelated["total_sessions"]!.GetValue<int>() == 1
                  && lateRelated["items"]!.AsArray().Where(item => item!["kind"]!.GetValue<string>() == "session")
                      .All(item => item!["session_id"]!.GetValue<string>() != alpha),
                "related excludes the source session from referenced session descriptors");
            Add("a.md", alpha, Message(1, "2026-01-01T00:00:00Z", "TRACE first")
                               + Message(1, "2026-01-01T00:01:00Z", "TRACE second"));
            Store.Save(Path.Combine(root, "state", "manifest.json"), new Manifest { Documents = docs });
            var ambiguous = Read(history.Timeline(new TimelineRequest(["trace"], Json: true)).Value);
            Check(ambiguous["items"]!.AsArray().All(item => item!["read_hint"] is null
                    && item["position_warning"]!.GetValue<string>().Contains("ambiguous", StringComparison.Ordinal)),
                "timeline omits exact-message hints for duplicate message numbers");
            var many = string.Concat(Enumerable.Range(1, 40).Select(i =>
                Message(i, "2026-01-01T00:00:00Z", "TRACE " + string.Join(" ", Enumerable.Repeat("large payload", 80)))));
            Add("a.md", alpha, many);
            Store.Save(Path.Combine(root, "state", "manifest.json"), new Manifest { Documents = docs });
            var page = history.Timeline(new TimelineRequest(["trace"], 50, Json: true));
            var pageData = Read(page.Value);
            Check(page.Value.Length < 12000 && pageData["output_truncated"]!.GetValue<bool>()
                  && pageData["next_cursor"] is not null,
                "timeline retains a continuation after its output budget cuts a page");
            var replacement = Message(1, "2026-01-01T00:00:00+00:00", "updated TRACE");
            Add("a.md", alpha, replacement);
            Store.Save(Path.Combine(root, "state", "manifest.json"), new Manifest { Documents = docs });
            Check(history.Timeline(new TimelineRequest(["trace"], 1, cursor, Json: true)).IsFailure,
                "timeline rejects a cursor after a source changes");
            var oversized = string.Join(" ", Enumerable.Repeat(new string('x', 1200), 30));
            Add("a.md", alpha, Message(1, "2026-01-01T00:00:00Z", "TRACE " + oversized));
            Store.Save(Path.Combine(root, "state", "manifest.json"), new Manifest { Documents = docs });
            var bounded = history.Timeline(new TimelineRequest(["trace"], 50, Json: true));
            Check(bounded.IsSuccess && bounded.Value.Length < 12000 && bounded.Value.Contains("omitted]", StringComparison.Ordinal),
                "navigation clips long original messages within the output limit");
            var cache = Path.Combine(root, "state", "conversation-search.sqlite");
            using var db = new SqliteReader(cache);
            var before = (long)db.Query("SELECT count(*) FROM files").Single()[0]!;
            File.WriteAllText(Path.Combine(root, "state", "manifest.json"), "{}");
            Check(history.Timeline(new TimelineRequest(["trace"], Json: true)).IsFailure
                  && (long)db.Query("SELECT count(*) FROM files").Single()[0]! == before,
                "invalid manifest fails before the cache can be purged");
        }
    }
}
