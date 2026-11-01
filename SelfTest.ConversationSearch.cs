using System.Text.Json.Nodes;

namespace SessionMemory
{
    internal static partial class SelfTest
    {
        private static void ConversationSearchChecks()
        {
            ConcurrentConversationCacheChecks();
            NavigationProjectionChecks();
            var temporary = Directory.CreateTempSubdirectory("conversation-search-test-");
            using var cleanup = new TemporaryDirectory(temporary.FullName);
            var corpus = Directory.CreateDirectory(Path.Combine(temporary.FullName, "corpus")).FullName;
            var docs = new Dictionary<string, DocumentMeta>(StringComparer.Ordinal);

            string Message(int n, string role, string date, string text, int part = 1, int count = 1) =>
                $"## Message {n:000000} part {part}/{count}\nrole: {role}\ntimestamp: {date}\ntext:\n{text}\n\n";

            void Add(string name, string body)
            {
                File.WriteAllText(Path.Combine(corpus, name), body);
                docs[name] = new DocumentMeta
                {
                    SessionId = name,
                    Sha256 = Transcript.Sha256(body),
                    Title = "Example"
                };
            }

            JsonObject[] Find(string q, IReadOnlySet<string>? visible = null, IReadOnlySet<string>? indexable = null, string? exclude = null, bool latest = false) =>
                ConversationSearch.Retrieve(temporary.FullName, q, docs, visible ?? docs.Keys.ToHashSet(), [], 8, latest, indexable, exclude).ToArray();

            var content = Message(1, "user", "2026-04-28T00:00:00Z", "Why is ProjectAlpha 1.3.0 broken?")
                          + Message(2, "assistant", "2026-04-28T00:01:00Z", "The first fact explains ", 1, 2)
                          + Message(2, "assistant", "2026-04-28T00:01:00Z", "the second fact.", 2, 2)
                          + Message(3, "user", "2026-04-28T00:02:00Z", "An unrelated question")
                          + Message(4, "assistant", "2026-04-28T00:03:00Z", "Different response.");
            Add("sample.md", content);
            var result = Find("ProjectAlpha 1.3.0");
            Check(result.Length > 0 && result.Any(h => h["text"].AsString()!.Contains("first fact explains \nthe second fact.")),
                "retrieves adjacent legacy multipart messages with their whitespace and source citations");
            Check(Find("April 28 ProjectAlpha").Length > 0 && Find("project alpha 1.3.0").Length > 0,
                "indexes date aliases and identifier components");
            Check(Find("' OR \"ProjectAlpha\" *").Length > 0, "quotes FTS input and tolerates punctuation");
            Add("older.md", Message(1, "assistant", "2024-01-01", "UniqueChronology original"));
            Add("newer.md", Message(1, "assistant", "2028-01-01", "UniqueChronology revised"));
            Check(Find("UniqueChronology", latest: true)[0]["file"].AsString() == "newer.md",
                "latest ranks actual matching message dates");
            Add("lexical.md", Message(1, "assistant", "2024-01-01", "A quartz retry limit was discussed."));
            Add("semantic.md", Message(4, "assistant", "2028-01-01T00:00:00Z", "The service waits longer before another attempt."));
            var semanticHit = new JsonObject
            {
                ["file"] = "semantic.md",
                ["text"] = "timestamp: 2028-01-01T00:00:00Z\ntext:\nThe service waits longer\n  before another attempt."
            };
            var hybrid = ConversationSearch.Retrieve(temporary.FullName, "quartz retry limit", docs,
                new HashSet<string>
                {
                    "lexical.md",
                    "semantic.md"
                }, [semanticHit], 8, false);
            Check(hybrid.Any(h => h["file"].AsString() == "semantic.md") && hybrid.Any(h => h["file"].AsString() == "lexical.md"),
                "fuses a verified semantic paraphrase with lexical results");
            var newest = ConversationSearch.Retrieve(temporary.FullName, "quartz retry limit", docs,
                new HashSet<string>
                {
                    "lexical.md",
                    "semantic.md"
                }, [semanticHit], 1, true);
            Check(newest.Length == 1 && newest[0]["file"].AsString() == "semantic.md", "ranks the actual semantic message date");
            semanticHit["text"] = "A completely fabricated message with no original match.";
            Check(ConversationSearch.Retrieve(temporary.FullName, "quartz retry limit", docs,
                new HashSet<string>
                    {
                        "lexical.md",
                        "semantic.md"
                    }, [semanticHit], 8, false)
                .All(h => h["file"].AsString() != "semantic.md"), "rejects unverified semantic passages");
            var encoded = new JsonObject
            {
                ["file"] = "semantic.md",
                ["semantic_passage"] = $"semantic.md__h{docs["semantic.md"].Sha256[..24]}__m000004_p001.md",
                ["text"] = "## Message 000004 part 1/1\nrole: assistant\ntext_length: 999\ntext:\nThe service"
            };
            var headerOnly = ConversationSearch.Retrieve(temporary.FullName, "quartz retry limit", docs,
                new HashSet<string> { "lexical.md", "semantic.md" }, [encoded], 8, false);
            Check(headerOnly.Any(h => h["file"].AsString() == "semantic.md"
                                      && h["text"].AsString()!.Contains("The service waits longer before another attempt.")),
                "resolves a verified truncated semantic hit to the original message");
            encoded["semantic_passage"] = $"semantic.md__h{new string('0', 24)}__m000004_p001.md";
            encoded["text"] = "The service waits longer before another attempt.";
            Check(ConversationSearch.Retrieve(temporary.FullName, "quartz retry limit", docs,
                new HashSet<string> { "lexical.md", "semantic.md" }, [encoded], 8, false)
                .All(h => h["file"].AsString() != "semantic.md"), "rejects stale encoded hits even when their text matches");
            encoded["semantic_passage"] = $"unknown.md__h{docs["semantic.md"].Sha256[..24]}__m000004_p001.md";
            Check(ConversationSearch.Retrieve(temporary.FullName, "quartz retry limit", docs,
                new HashSet<string> { "lexical.md", "semantic.md" }, [encoded], 8, false)
                .All(h => h["file"].AsString() != "semantic.md"), "rejects encoded hits naming an unknown source");
            Check(Find("ProjectAlpha not different response").Any(h => h["file"].AsString() == "sample.md"),
                "natural language contrast does not exclude a source");
            Check(Find("ProjectAlpha", exclude: "Different response").All(h => !h["text"].AsString()!.Contains("Different response")),
                "explicit literal exclusion filters matching windows");
            Add("excluded-many.md", string.Concat(Enumerable.Range(1, 210).Select(n =>
                Message(n, "assistant", "", "FilterSignal FilterSignal FilterSignal omit-topic"))));
            Add("included.md", Message(1, "assistant", "", "FilterSignal remains."));
            Check(Find("FilterSignal", new HashSet<string> { "excluded-many.md", "included.md" }, exclude: "OMIT-TOPIC")
                .Any(hit => hit["file"].AsString() == "included.md"), "continues past excluded ranked windows");
            Add("leading.md", Message(1, "assistant", "", "LeadingSignal " + string.Concat(Enumerable.Repeat("more text ", 200))));
            Check(Find("LeadingSignal", new HashSet<string> { "leading.md" })
                .Any(hit => hit["text"].AsString()!.Contains("LeadingSignal", StringComparison.Ordinal)),
                "preserves the first character when clipping from the start");
            Add("repeated.md", Message(1, "assistant", "", string.Concat(Enumerable.Repeat("RepeatSignal ", 10000))));
            Check(Find("RepeatSignal", new HashSet<string> { "repeated.md" })
                .Any(hit => hit["text"].AsString()!.Contains("RepeatSignal", StringComparison.Ordinal)),
                "clips a message with many repeated matches");
            Add("hidden.md", Message(1, "assistant", "", "ProjectAlpha hidden payload"));
            var visible = new HashSet<string>
            {
                "sample.md"
            };
            Check(Find("ProjectAlpha", visible, docs.Keys.ToHashSet()).All(h => h["file"].AsString() != "hidden.md"),
                "SQL restricts results to visible sources");
            Find("ProjectAlpha", visible, visible);
            using (var db = new SqliteReader(Path.Combine(temporary.FullName, "state", "conversation-search.sqlite")))
                Check(!db.Query("SELECT name FROM files WHERE name=?", "hidden.md").Any(), "purges non-indexable cache rows");

            var revised = Message(1, "assistant", "", "Replacement phrase. The answer is forty two.");
            Add("sample.md", revised);
            Check(Find("Replacement phrase").Any(h => h["text"].AsString()!.Contains("forty two")),
                "refreshes changed source documents");
            File.WriteAllText(Path.Combine(corpus, "sample.md"), "tampered");
            Check(Find("Replacement phrase").All(h => h["file"].AsString() != "sample.md"), "purges hash-mismatched source");
            using (var db = new SqliteReader(Path.Combine(temporary.FullName, "state", "conversation-search.sqlite")))
                Check(!db.Query("SELECT name FROM files WHERE name=?", "sample.md").Any(), "does not retain mismatched cached content");

            var longText = string.Join(" ", Enumerable.Repeat("An earlier sentence offers context.", 45))
                           + " DistinctiveSignal is corrected by the second fact. "
                           + string.Join(" ", Enumerable.Repeat("Later details remain here.", 50));
            Add("long.md", Message(1, "", "", longText));
            foreach (var n in Enumerable.Range(0, 8))
                Add($"glyph{n}.md", Message(1, "assistant", "2026-01-01", "DistinctiveSignal " + string.Concat(Enumerable.Repeat("✅…≤", 700))));
            var bounded = Find("DistinctiveSignal");
            var rendered = Indexer.RenderText("cli", [.. bounded], docs);
            Check(rendered.Length <= 12000 && bounded.All(h => Compact.Segments(h["text"].AsString()!).Length > 0),
                "bounds actual rendered text including expanded characters");
            Check(Find("DistinctiveSignal", new HashSet<string>
                {
                    "long.md"
                }).Any(h => h["text"].AsString()!.Contains("DistinctiveSignal")),
                "clips oversized messages around matching terms");
            Check(Find("nonsense", new HashSet<string>()).Length == 0, "returns no sources for empty visibility");
            foreach (var n in Enumerable.Range(0, 12)) Add($"many{n}.md", Message(1, "assistant", "", "SaffronSignal concise result"));
            var many = docs.Keys.Where(name => name.StartsWith("many", StringComparison.Ordinal)).ToHashSet();
            Check(ConversationSearch.Retrieve(temporary.FullName, "SaffronSignal", docs, many, [], 12, false).Length == 12,
                "respects result limits above eight when the display budget permits");
            docs["../outside.md"] = new DocumentMeta
            {
                SessionId = "outside"
            };
            File.WriteAllText(Path.Combine(temporary.FullName, "outside.md"), Message(1, "assistant", "", "OutsideMarker"));
            Check(Find("OutsideMarker").Length == 0, "rejects source paths outside the corpus");
            var empty = new HashSet<string>();
            Find("SaffronSignal", many, many);
            Find("SaffronSignal", empty, many);
            using (var db = new SqliteReader(Path.Combine(temporary.FullName, "state", "conversation-search.sqlite")))
                Check(db.Query("SELECT count(*) FROM files").Single()[0] is long count && count > 0,
                    "empty query visibility preserves globally indexable cache entries");
            Find("SaffronSignal", empty, empty);
            using (var db = new SqliteReader(Path.Combine(temporary.FullName, "state", "conversation-search.sqlite")))
                Check((long)db.Query("SELECT count(*) FROM files").Single()[0]! == 0
                      && (long)db.Query("SELECT count(*) FROM windows").Single()[0]! == 0,
                    "empty global allowlist purges cached source and window rows");
            var progress = new List<(int, int)>();
            ConversationSearch.Prepare(temporary.FullName, docs, many, (done, total) => progress.Add((done, total)));
            Check(progress[0] == (0, many.Count) && progress[^1] == (many.Count, many.Count), "prepares the cache with session progress");
            using (var db = new SqliteReader(Path.Combine(temporary.FullName, "state", "conversation-search.sqlite")))
                Check((long)db.Query("SELECT count(*) FROM files").Single()[0]! == many.Count, "prepare indexes all allowed files");
            var cache = Path.Combine(temporary.FullName, "state", "conversation-search.sqlite");
            string[] before;
            using (var db = SqliteReader.Create(cache))
            {
                before = db.Query("SELECT rowid,body,file,messages FROM windows ORDER BY rowid").Select(row => string.Join("\0", row)).ToArray();
                db.Execute("DROP TABLE window_sources");
                db.Execute("PRAGMA user_version = 1");
            }
            ConversationSearch.Prepare(temporary.FullName, docs, many);
            using (var db = new SqliteReader(cache))
            {
                var after = db.Query("SELECT rowid,body,file,messages FROM windows ORDER BY rowid").Select(row => string.Join("\0", row)).ToArray();
                Check(before.SequenceEqual(after), "migrates version one without rewriting FTS rows");
                Check((long)db.Query("SELECT count(*) FROM window_sources").Single()[0]! == before.Length, "backfills every window mapping");
                Check(db.Query("EXPLAIN QUERY PLAN SELECT id FROM window_sources WHERE file=?", "many0.md")
                    .Any(row => row.Any(value => value is string s && s.Contains("window_sources_file", StringComparison.Ordinal))),
                    "uses the file index for window lookup");
            }
            Add("many0.md", Message(1, "assistant", "", "SaffronSignal changed result"));
            var reduced = many.Where(name => name != "many1.md").ToHashSet();
            ConversationSearch.Prepare(temporary.FullName, docs, reduced);
            using (var db = new SqliteReader(cache))
            {
                Check(!db.Query("SELECT id FROM window_sources WHERE file=?", "many1.md").Any(), "deletes removed file mappings");
                Check(db.Query("SELECT windows.body FROM window_sources JOIN windows ON windows.rowid=window_sources.id WHERE window_sources.file=?", "many0.md")
                    .Any(row => ((string)row[0]!).Contains("changed result", StringComparison.Ordinal)), "updates changed file through indexed mapping");
                Check((long)db.Query("SELECT count(*) FROM window_sources LEFT JOIN windows ON windows.rowid=window_sources.id WHERE windows.rowid IS NULL").Single()[0]! == 0
                    && (long)db.Query("SELECT count(*) FROM windows LEFT JOIN window_sources ON window_sources.id=windows.rowid WHERE window_sources.id IS NULL").Single()[0]! == 0,
                    "keeps FTS rows and mappings consistent");
            }
            ConversationSearch.Prepare(temporary.FullName, docs, empty);
            using (var db = new SqliteReader(Path.Combine(temporary.FullName, "state", "conversation-search.sqlite")))
                Check((long)db.Query("SELECT count(*) FROM files").Single()[0]! == 0, "prepare purges an empty allowlist");
            var linkRoot = Directory.CreateTempSubdirectory("conversation-link-test-");
            using var linkCleanup = new TemporaryDirectory(linkRoot.FullName);
            Directory.CreateDirectory(Path.Combine(linkRoot.FullName, "state"));
            var sourceLink = Path.Combine(corpus, "source-link.md");
            var cacheLink = Path.Combine(linkRoot.FullName, "state", "conversation-search.sqlite");
            var target = Path.Combine(temporary.FullName, "outside.md");
            var linksCreated = false;
            try
            {
                File.CreateSymbolicLink(sourceLink, target);
                File.CreateSymbolicLink(cacheLink, target);
                linksCreated = true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                Console.Error.WriteLine("Self-test: conversation source and cache symbolic-link checks skipped; links cannot be created.");
            }
            if (linksCreated)
            {
                docs["source-link.md"] = new DocumentMeta
                {
                    SessionId = "link"
                };
                Check(Find("OutsideMarker", new HashSet<string>
                {
                    "source-link.md"
                }).Length == 0, "does not follow a corpus file link");
                var rejected = false;
                try { ConversationSearch.Prepare(linkRoot.FullName, docs, many); }
                catch (IOException) { rejected = true; }
                Check(rejected, "does not follow a cache file link");
            }
        }

        private static void NavigationProjectionChecks()
        {
            var temporary = Directory.CreateTempSubdirectory("navigation-projection-test-");
            using var cleanup = new TemporaryDirectory(temporary.FullName);
            var corpus = Directory.CreateDirectory(Path.Combine(temporary.FullName, "corpus")).FullName;
            var first = "## Message 000001 part 1/2\nrole: user\ntimestamp: 2026-04-28T12:00:00Z\ntext:\nCaf\u00e9 migration https://EXAMPLE.test/a?x=1#one\n\nAnother paragraph.\n\n"
                        + "## Message 000001 part 2/2\nrole: user\ntimestamp: 2026-04-28T12:00:00Z\ntext:\nRelated 12345678-1234-1234-1234-123456789abc:subagent:worker\n";
            var second = "## Message 000001 part 1/1\nrole: assistant\ntimestamp: 2026-04-28T12:00:00\ntext:\nCafe\u0301 migration https://example.test/a?x=2#one https://example.test/b?x=1#one\n";
            File.WriteAllText(Path.Combine(corpus, "first.md"), first);
            File.WriteAllText(Path.Combine(corpus, "second.md"), second);
            var docs = new Dictionary<string, DocumentMeta>
            {
                ["first.md"] = new() { SessionId = "first", Sha256 = Transcript.Sha256(first) },
                ["second.md"] = new() { SessionId = "second", Sha256 = Transcript.Sha256(second) }
            };
            var allowed = docs.Keys.ToHashSet();
            ConversationSearch.Prepare(temporary.FullName, docs, allowed);
            var cache = Path.Combine(temporary.FullName, "state", "conversation-search.sqlite");
            string[] rowsBefore;
            string[] rankingBefore;
            using (var db = SqliteReader.Create(cache))
            {
                rowsBefore = db.Query("SELECT rowid,body,file,messages FROM windows ORDER BY rowid").Select(row => string.Join("\0", row)).ToArray();
                rankingBefore = db.Query("SELECT rowid,bm25(windows) FROM windows WHERE windows MATCH ? ORDER BY bm25(windows)", "migration").Select(row => string.Join("\0", row)).ToArray();
                db.Execute("DROP TABLE nav_refs");
                db.Execute("DROP TABLE nav_messages");
                db.Execute("PRAGMA user_version = 2");
            }
            ConversationSearch.Prepare(temporary.FullName, docs, allowed);
            using (var db = new SqliteReader(cache))
            {
                Check(rowsBefore.SequenceEqual(db.Query("SELECT rowid,body,file,messages FROM windows ORDER BY rowid").Select(row => string.Join("\0", row)))
                    && rankingBefore.SequenceEqual(db.Query("SELECT rowid,bm25(windows) FROM windows WHERE windows MATCH ? ORDER BY bm25(windows)", "migration").Select(row => string.Join("\0", row))),
                    "rebuilding an older cache preserves search contents and ranks");
                var messages = db.Query("SELECT file,number,utc_ticks,text,folded FROM nav_messages ORDER BY file").ToArray();
                Check(messages.Length == 2 && ((string)messages[0][3]!).Contains("\nRelated", StringComparison.Ordinal)
                    && ((string)messages[0][3]!).Contains("\n\nAnother paragraph.", StringComparison.Ordinal), "navigation preserves paragraphs and joins message parts");
                Check(messages[0][2] is long && messages[1][2] is null, "navigation preserves UTC and missing dates");
                Check(((string)messages[0][4]!).Contains(ConversationSearch.FoldNavigationText("caf\u00e9"), StringComparison.Ordinal)
                    && ((string)messages[1][4]!).Contains(ConversationSearch.FoldNavigationText("cafe\u0301"), StringComparison.Ordinal)
                    && !((string)messages[0][4]!).Contains(ConversationSearch.FoldNavigationText("cafe\u0301"), StringComparison.Ordinal),
                    "navigation applies invariant case matching to literal Unicode code points");
                var uris = db.Query("SELECT target FROM nav_refs ORDER BY target").Select(row => (string)row[0]!).ToArray();
                Check(uris.Length == 3 && uris.Contains("https://example.test/a?x=1#one") && uris.Contains("https://example.test/a?x=2#one")
                    && uris.Contains("https://example.test/b?x=1#one"), "navigation preserves distinct resource paths and queries");
                Check(ConversationSearch.NormalizeNavigationUri("HTTPS://Example.test/a/../b?key=%2f#Part") == "https://example.test/a/../b?key=%2f#Part"
                    && ConversationSearch.NormalizeNavigationUri("https://user@example.test/b") is null
                    && ConversationSearch.NavigationUris("[link](https://example.test/b?x=1#one)").Contains("https://example.test/b?x=1#one"),
                    "URI normalization preserves path bytes and strips only unmatched Markdown closing parentheses");
            }
            File.WriteAllText(Path.Combine(corpus, "first.md"), "changed without manifest update");
            ConversationSearch.Prepare(temporary.FullName, docs, allowed);
            using (var stale = new SqliteReader(cache))
                Check(!stale.Query("SELECT id FROM nav_messages WHERE file=?", "first.md").Any()
                    && (long)stale.Query("SELECT count(*) FROM nav_refs LEFT JOIN nav_messages ON nav_messages.id=nav_refs.message_id WHERE nav_messages.id IS NULL").Single()[0]! == 0,
                    "hash mismatch purges stale navigation without orphan references");
            ConversationSearch.Prepare(temporary.FullName, docs, new HashSet<string>());
            using var check = new SqliteReader(cache);
            Check((long)check.Query("SELECT count(*) FROM nav_messages").Single()[0]! == 0
                && (long)check.Query("SELECT count(*) FROM nav_refs").Single()[0]! == 0,
                "stale hash and global exclusion purge navigation and references");
        }

        private static void ConcurrentConversationCacheChecks()
        {
            var temporary = Directory.CreateTempSubdirectory("conversation-concurrency-test-");
            using var cleanup = new TemporaryDirectory(temporary.FullName);
            var corpus = Directory.CreateDirectory(Path.Combine(temporary.FullName, "corpus")).FullName;
            var docs = new Dictionary<string, DocumentMeta>();
            for (var i = 0; i < 4; i++)
            {
                var name = $"source{i}.md";
                var text = $"## Message 000001 part 1/1\nrole: assistant\ntimestamp: 2026-01-01\ntext:\nConcurrent evidence {i}.\n";
                File.WriteAllText(Path.Combine(corpus, name), text);
                docs[name] = new DocumentMeta { SessionId = name, Sha256 = Transcript.Sha256(text) };
            }
            var allowed = docs.Keys.ToHashSet();
            var cache = Path.Combine(temporary.FullName, "state", "conversation-search.sqlite");

            void RunTogether(string stage)
            {
                using var ready = new CountdownEvent(2);
                using var start = new ManualResetEventSlim(false);
                var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
                {
                    ready.Signal();
                    if (!start.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Concurrent test start timed out.");
                    ConversationSearch.Prepare(temporary.FullName, docs, allowed);
                })).ToArray();
                var bothReady = ready.Wait(TimeSpan.FromSeconds(15));
                start.Set();
                Check(bothReady && Task.WaitAll(tasks, TimeSpan.FromSeconds(30)), $"{stage} completes concurrently");
                using var db = new SqliteReader(cache);
                Check((long)db.Query("SELECT count(*) FROM files").Single()[0]! == 4
                    && (long)db.Query("SELECT count(*) FROM windows").Single()[0]! == 4
                    && (long)db.Query("SELECT count(*) FROM window_sources").Single()[0]! == 4
                    && (long)db.Query("SELECT count(*) FROM nav_messages").Single()[0]! == 4
                    && (long)db.Query("SELECT count(*) FROM nav_refs LEFT JOIN nav_messages ON nav_messages.id=nav_refs.message_id WHERE nav_messages.id IS NULL").Single()[0]! == 0
                    && (long)db.Query("SELECT count(*) FROM windows LEFT JOIN window_sources ON window_sources.id=windows.rowid WHERE window_sources.id IS NULL").Single()[0]! == 0
                    && (long)db.Query("SELECT count(*) FROM window_sources LEFT JOIN windows ON windows.rowid=window_sources.id WHERE windows.rowid IS NULL").Single()[0]! == 0,
                    $"{stage} retains all documents with no orphan rows");
                Check(docs.All(pair => Transcript.Sha256(File.ReadAllText(Path.Combine(corpus, pair.Key))) == pair.Value.Sha256),
                    $"{stage} does not alter source files");
            }

            RunTogether("fresh cache");
            using (var db = SqliteReader.Create(cache))
            {
                db.Execute("DROP TABLE window_sources");
                db.Execute("PRAGMA user_version = 1");
            }
            RunTogether("incompatible cache rebuild");
        }
    }
}
