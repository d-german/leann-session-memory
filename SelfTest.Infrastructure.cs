using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace SessionMemory
{
    internal static partial class SelfTest
    {
        // Indexer, search server and watcher behavior after the move to records, immutable collections and Result.
        private static void InfrastructureRefactorChecks()
        {
            InfrastructureFilterChecks();
            InfrastructureHitsChecks();
            var temporary = Directory.CreateTempSubdirectory("session-memory-infrastructure-");
            try
            {
                InfrastructureStateChecks(Path.Combine(temporary.FullName, "state"));
                InfrastructureSearchChecks(Path.Combine(temporary.FullName, "search"));
                InfrastructureWatcherChecks(Path.Combine(temporary.FullName, "watcher"), Path.Combine(temporary.FullName, "failing"));
            }
            finally
            {
                temporary.Delete(recursive: true);
            }
        }

        private static void InfrastructureFilterChecks()
        {
            ImmutableArray<string> both = ["recent", "archive"];
            var none = FrozenSet<string>.Empty;
            var empty = Indexer.Filter([], both, none, none, 5);
            Check(empty is { IsDefault: false, IsEmpty: true }, "filters no hits into an empty array");
            var labelled = Indexer.Filter([Hit("a.md", "session-memory"), Hit("b.md", "session-memory-recent"), Hit("c.md", "session-memory")], both, none, none, 2);
            Check(labelled.Select(hit => $"{hit["tier"].AsString()}:{hit["file"].AsString()}").SequenceEqual(["archive:a.md", "recent:b.md"]),
                "labels each hit with its index's tier and stops at top-k");
            Check(Indexer.Filter([Hit("a.md", "another-index")], ["recent"], none, none, 5).Single()["tier"].AsString() == "recent",
                "labels a hit from an unknown index with the searched tier");
            Check(Indexer.Filter([Hit("a.md", null)], both, none, none, 0).IsEmpty, "keeps nothing for top-k 0");
            FrozenSet<string> recent = ["a.md"];
            FrozenSet<string> current = ["a.md", "b.md"];
            var kept = Indexer.Filter([Hit("a.md", "session-memory"), Hit("a.md", "session-memory-recent"), Hit("gone.md", "session-memory-recent"), Hit("b.md", "session-memory")],
                both, recent, current, 5);
            Check(kept.Select(hit => $"{hit["tier"].AsString()}:{hit["file"].AsString()}").SequenceEqual(["recent:a.md", "archive:b.md"]),
                "drops stale archive copies and sessions outside the visible set");
            var sessions = new Dictionary<string, DocumentMeta>
            {
                ["parent.md"] = new()
                {
                    SessionId = "abcdef12-3456"
                },
                ["other.md"] = new()
                {
                    SessionId = "99999999-0000"
                }
            };
            Check(Indexer.Visible(sessions, [], null).SetEquals(["parent.md", "other.md"]), "shows every session when nothing is excluded");
            Check(Indexer.Visible(sessions, ["ABCDEF12"], null).SetEquals(["other.md"]), "hides excluded sessions by prefix, ignoring case");
            Check(Indexer.Visible(sessions, [], "zzzzzzzz").Count == 0, "shows nothing for a session with no indexed document");
        }

        private static void InfrastructureHitsChecks()
        {
            var found = Indexer.Hits(new LeannOutput(0, """{"results":[{"file":"a.md"},{"file":"b.md"}]}""", ""));
            Check(found.IsSuccess && found.Value.Count == 2, "reads LEANN's ranked hits");
            string[] broken = ["", "not json", "null", """{"results":{}}""", """{"other":[]}"""];
            Check(broken.All(output => Indexer.Hits(new LeannOutput(3, output, "boom")) is { IsFailure: true, Error: "LEANN search failed with exit code 3: boom" }),
                "fails output without a results array with LEANN's exit code and error");
        }

        private static void InfrastructureStateChecks(string root)
        {
            Check(Indexer.LoadState(root, "recent") is { IndexName: "", BuildId: "", PublishedAt: "", CorpusHash: "", Signature: "", DurationSeconds: 0, Documents.Count: 0 },
                "reads a missing tier state as empty");
            var path = Indexer.StatePath(root, "archive");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{"index_name": "session-memory", "duration_seconds": 1.5}""");
            Check(Store.Load<TierState>(path) is { IndexName: "session-memory", BuildId: "", PublishedAt: "", CorpusHash: "", Signature: "", DurationSeconds: 1.5, Documents.Count: 0 },
                "fills tier-state fields missing from the file");
            Store.Save(path, new TierState
            {
                IndexName = "session-memory",
                BuildId = "b",
                PublishedAt = "2026-10-26T00:00:00+00:00",
                CorpusHash = "c",
                Signature = "s",
                DurationSeconds = 2.5,
                Documents = new Dictionary<string, string>
                {
                    ["z.md"] = "1",
                    ["a.md"] = "2"
                }
            });
            Check(Store.LoadNode(path) is JsonObject saved
                  && saved.Select(pair => pair.Key).SequenceEqual(["index_name", "build_id", "published_at", "corpus_hash", "signature", "duration_seconds", "documents"]),
                "keeps tier-state property names and order");
            Check(Indexer.LoadState(root, "archive") is { Signature: "s", DurationSeconds: 2.5 } reloaded && reloaded.Documents.Keys.SequenceEqual(["z.md", "a.md"]),
                "keeps tier-state documents in order");
            Directory.CreateDirectory(Path.Combine(root, "corpus"));
            File.WriteAllText(Path.Combine(root, "corpus", "a.md"), "a");
            var staged = Indexer.Stage(root, "recent", new Dictionary<string, string>
            {
                ["a.md"] = "1",
                ["gone.md"] = "2"
            }, "hash");
            Check(staged is { Tier: "recent", CorpusHash: "hash" } && staged.Documents.Keys.SequenceEqual(["a.md"]) && File.Exists(Path.Combine(staged.BuildRoot, "docs", "a.md")),
                "stages only documents still in the corpus");
        }

        private static void InfrastructureSearchChecks(string root)
        {
            Check(new SearchRequest
            {
                Root = root,
                Query = "q"
            } is { TopK: 8, Json: false, IncludeCurrent: false, OnlySession: null }, "defaults a search to eight results");
            using var error = new StringWriter();
            var standardError = Console.Error;
            int exitCode;
            Console.SetError(error);
            try
            {
                exitCode = Indexer.Search(new SearchRequest
                {
                    Root = root,
                    Query = "canary",
                    TopK = 3,
                    Json = true,
                    IncludeCurrent = true,
                    OnlySession = "abcdef12"
                });
            }
            finally
            {
                Console.SetError(standardError);
            }
            Check(exitCode == 1 && error.ToString().Trim() == "No session-memory index has been built yet.", "fails a search before any index is built");
            // Only probes the health endpoint: a server for another data root, or none at all, answers nothing for this one.
            Check(SearchServer.Search(root, "session-memory", "canary", 1) is null, "leaves another data root's search server alone");
        }

        private static void InfrastructureWatcherChecks(string root, string failing)
        {
            // Both indexes exist and match the empty corpus, so the one-time refresh starts no LEANN build.
            Directory.CreateDirectory(Path.Combine(root, ".leann", "indexes", Indexer.IndexName("archive")));
            Directory.CreateDirectory(Path.Combine(root, ".leann", "indexes", Indexer.IndexName("recent")));
            Store.Save(Indexer.StatePath(root, "archive"), new TierState
            {
                Documents = new Dictionary<string, string>
                {
                    ["kept.md"] = "1"
                }
            });
            Store.Save(Indexer.StatePath(root, "recent"), new TierState
            {
                Signature = Indexer.Signature(new Dictionary<string, string>())
            });
            Check(new Watcher(root, []).RunAsync(true, false).GetAwaiter().GetResult() == 0, "refreshes once without rebuilding unchanged indexes");
            var status = Store.LoadNode(Path.Combine(root, "state", "watcher.json"));
            Check(status?["capture"]?["reparsed"] is JsonArray { Count: 0 } && status["building"] is JsonArray { Count: 0 } && status["errors"] is JsonObject { Count: 0 },
                "writes the watcher status");
            Check(File.ReadAllText(Path.Combine(root, "logs", "watcher.log")).Contains("one-time refresh started"), "logs the refresh");
            var broken = new Family("broken", () => throw new IOException("sources unreadable"), () => throw new InvalidOperationException("never parsed"));
            Check(new Watcher(failing, [broken]).RunAsync(true, false).GetAwaiter().GetResult() == 1
                  && File.ReadAllText(Path.Combine(failing, "logs", "watcher.log")).Contains("cycle failed: IOException: sources unreadable"),
                "records a failed cycle as its exception type and message");
        }
    }
}
