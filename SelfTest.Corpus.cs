using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SessionMemory.Clients;

namespace SessionMemory
{
    internal static partial class SelfTest
    {
        private static void CorpusRefactorChecks()
        {
            CorpusRecordChecks();
            CorpusTimestampChecks();
            var temporary = Directory.CreateTempSubdirectory("session-memory-corpus-test-");
            using var cleanup = new TemporaryDirectory(temporary.FullName);
            CorpusStoreChecks(Path.Combine(temporary.FullName, "store"));
            CorpusExclusionChecks(Path.Combine(temporary.FullName, "exclusions"));
            CorpusCaptureChecks(Path.Combine(temporary.FullName, "capture"));
        }

        private static void CorpusRecordChecks()
        {
            var message = new Message("1", "user", "hi", 0, "");
            var later = message with
            {
                Sequence = 1
            };
            Check(message == new Message("1", "user", "hi", 0, "") && message != later && later.Text == "hi", "compares messages by value");
            var document = new Document("f", "c", "s", "ref", [message]);
            var titled = document with
            {
                Title = "t"
            };
            Check(titled.Messages == document.Messages && document.Title == "" && titled.Title == "t", "copies a document with a new title");
            Check(Transcript.SplitText("") is [""] && Transcript.SplitText("short") is ["short"], "keeps short text as one part");
            Check(Transcript.SplitText(new string('a', 901)) is { IsDefault: false, Length: 2 }, "splits text over the limit");
            var report = JsonSerializer.Deserialize<FamilyReport>("{}", Store.Options);
            Check(report is { Ok: true, Error: "", Fingerprint: null, Stats.Count: 0 }, "fills a family report's missing fields");
            var failed = JsonSerializer.Deserialize<FamilyReport>("""{"ok": false, "stats": {"files": 1}}""", Store.Options);
            Check(failed is { Ok: false, Error: "", Fingerprint: null, Stats.Count: 1 }, "reads a failed family report without an error text");
            var manifest = JsonSerializer.Deserialize<Manifest>("{}", Store.Options);
            Check(manifest is { Version: 0, GeneratedAt: "", CorpusHash: "", Documents.Count: 0, Families.Count: 0 }, "fills a manifest's missing fields");
            var meta = JsonSerializer.Deserialize<DocumentMeta>("""{"session_id": "s"}""", Store.Options);
            Check(meta is { SessionId: "s", Family: "", Client: "", SourceRef: "", Sha256: "", LastActivity: "", Title: "" }, "fills a document's missing fields");
        }

        // The expected texts are what the try/catch conversion this range check replaced returned for the same input.
        private static void CorpusTimestampChecks()
        {
            const string lastMillisecond = "9999-12-31T23:59:59.999+00:00";
            (double? Milliseconds, string Expected, string Name)[] cases =
            [
                (253402300799999.4, lastMillisecond, "formats the last millisecond (.4)"),
                (253402300799999.5, lastMillisecond, "formats the last millisecond (.5)"),
                (253402300799999.6, lastMillisecond, "formats the last millisecond (.6)"),
                (253402300799999, "9999-12-31T23:59:59.998+00:00", "keeps rounding just below the last millisecond"),
                (253402300800000, "", "gives no timestamp past year 9999"),
                (1e300, "", "gives no timestamp for a huge value"),
                (double.MaxValue, "", "gives no timestamp for the largest double"),
                (-1, "", "gives no timestamp for a negative value"),
                (double.NaN, "", "gives no timestamp for NaN"),
                (double.PositiveInfinity, "", "gives no timestamp for infinity"),
                (double.NegativeInfinity, "", "gives no timestamp for negative infinity"),
                (null, "", "gives no timestamp for a missing value"),
                (-0.0, "1970-01-01T00:00:00.000+00:00", "formats negative zero as the epoch"),
                (999.9995, "1970-01-01T00:00:01.000+00:00", "carries rounded microseconds into the next second")
            ];
            foreach (var (milliseconds, expected, name) in cases) Check(Transcript.IsoFromMs(milliseconds) == expected, name);
        }

        private static void CorpusStoreChecks(string root)
        {
            var path = Path.Combine(root, "state", "manifest.json");
            Store.Save(path, CorpusSampleManifest());
            var text = File.ReadAllText(path);
            CorpusManifestKeyChecks(text);
            var loaded = Store.Load<Manifest>(path);
            Check(loaded is not null && JsonSerializer.Serialize(loaded, Store.Options) == text, "round-trips the manifest unchanged");
            Check(loaded?.Families["claude"] is { Ok: false, Error: "IOException: The file is locked.", Fingerprint: null, Stats.Count: 0 }, "reads a failed family back");
            Check(Store.LoadNode(path)?["version"]?.GetValue<int>() == 3, "reads a state file as a node");
            var missing = Path.Combine(root, "missing.json");
            Check(Store.Load<Manifest>(missing) is null && Store.LoadNode(missing) is null, "reads a missing state file as absent");
            Check(Store.Load<List<string>>(Path.Combine(root, "no-such-folder", "excluded.json")) is null, "reads a missing state folder as absent");
            var corrupt = Path.Combine(root, "corrupt.json");
            File.WriteAllText(corrupt, "{\"version\": ");
            Check(Store.Load<Manifest>(corrupt) is null && Store.LoadNode(corrupt) is null, "reads a corrupt state file as absent");
            File.WriteAllText(corrupt, "[1, 2]");
            Check(Store.Load<Manifest>(corrupt) is null && Store.LoadNode(corrupt) is JsonArray { Count: 2 }, "reads a state file of another shape as absent");
            CorpusLockedFileChecks(path);
            Check(Store.Load<Manifest>(path) is { DocumentCount: 1 }, "reads the state file again once it is unlocked");
        }

        private static Manifest CorpusSampleManifest() => new()
        {
            Version = 3,
            GeneratedAt = "2026-10-26T00:00:00+00:00",
            CorpusHash = "hash",
            Documents = new Dictionary<string, DocumentMeta>
            {
                ["a.md"] = new()
                {
                    Family = "codex",
                    Client = "codex",
                    SessionId = "s",
                    SourceRef = "ref",
                    MessageCount = 2,
                    Sha256 = "sha",
                    Bytes = 3,
                    LastActivity = "2026-10-26T00:00:00Z",
                    Title = "t"
                }
            },
            Families = new Dictionary<string, FamilyReport>
            {
                ["codex"] = new()
                {
                    Fingerprint = "f",
                    Stats = new Dictionary<string, long>
                    {
                        ["sessions"] = 1,
                        ["files"] = 2,
                        ["malformed"] = 0
                    }
                },
                ["claude"] = new()
                {
                    Ok = false,
                    Error = "IOException: The file is locked."
                }
            },
            DocumentCount = 1,
            MessageCount = 2,
            Bytes = 3
        };

        private static void CorpusManifestKeyChecks(string text)
        {
            using var saved = JsonDocument.Parse(text);
            var top = saved.RootElement;
            Check(CorpusKeys(top).SequenceEqual(["version", "generated_at", "corpus_hash", "documents", "families", "document_count", "message_count", "bytes"]),
                "writes the manifest keys in order");
            Check(CorpusKeys(top.GetProperty("documents").GetProperty("a.md"))
                    .SequenceEqual(["family", "client", "session_id", "source_ref", "message_count", "sha256", "bytes", "last_activity", "title"]),
                "writes the document keys in order");
            var families = top.GetProperty("families");
            Check(CorpusKeys(families).SequenceEqual(["codex", "claude"]) && CorpusKeys(families.GetProperty("claude")).SequenceEqual(["ok", "error", "fingerprint", "stats"]),
                "writes the family keys in order");
            Check(CorpusKeys(families.GetProperty("codex").GetProperty("stats")).SequenceEqual(["sessions", "files", "malformed"]), "keeps the stats in insertion order");
            var failed = families.GetProperty("claude");
            Check(!failed.GetProperty("ok").GetBoolean() && failed.GetProperty("error").GetString() == "IOException: The file is locked."
                                                          && failed.GetProperty("fingerprint").ValueKind == JsonValueKind.Null, "writes a failed family's error");
        }

        private static void CorpusLockedFileChecks(string path)
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Check(Store.Load<Manifest>(path) is null && Store.LoadNode(path) is null, "reads a locked state file as absent");
        }

        private static IEnumerable<string> CorpusKeys(JsonElement element) => element.EnumerateObject().Select(property => property.Name);

        private static void CorpusExclusionChecks(string root)
        {
            List<string> saved = ["aaaaaaaa"];
            using var services = Services(Path.Combine(root, "home"), root);
            using var elsewhere = Services(Path.Combine(root, "home"), Path.Combine(root, "none"));
            var exclusions = services.GetRequiredService<ExclusionService>();
            Store.Save(exclusions.FilePath, saved);
            var excluded = exclusions.ForSearch(false, " bbbbbbbb-1 ");
            Check(excluded.SequenceEqual(["aaaaaaaa", "bbbbbbbb-1"]) && exclusions.Load().SequenceEqual(["aaaaaaaa"]),
                "leaves out the current session without saving it");
            Check(exclusions.ForSearch(true, "bbbbbbbb-1").SequenceEqual(["aaaaaaaa"])
                  && exclusions.ForSearch(false, "bbbb").SequenceEqual(["aaaaaaaa"]), "keeps the current session when asked or when its ID is too short");
            Check(elsewhere.GetRequiredService<ExclusionService>().ForSearch(true) is { IsDefault: false, IsEmpty: true }, "returns no exclusions without a file");
            Check(ExclusionService.IsExcluded("AAAAAAAA:subagent:x", excluded) && ExclusionService.IsExcluded("bbbbbbbb-1", excluded)
                                                                              && !ExclusionService.IsExcluded("bbbbbbbb-2", excluded),
                "matches exclusions by prefix, ignoring case");
            Check(!ExclusionService.IsExcluded("aaaaaaaa", []), "excludes nothing without exclusions");
        }

        private static void CorpusCaptureChecks(string root)
        {
            var good = new LambdaSessionClient("good", _ => [], _ => new FamilyResult([CorpusDocument("good", "good-session", "kept")], new Dictionary<string, long>
            {
                ["sessions"] = 1
            }));
            var broken = new LambdaSessionClient("broken", _ => [], _ => throw new InvalidOperationException("boom"));
            // The second document cannot render, so the family fails after the first one is already in the corpus.
            var partial = new LambdaSessionClient("partial", _ => [],
                _ => new FamilyResult([CorpusDocument("partial", "partial-1", "rendered"), CorpusDocument("partial", "partial-2", null!)], []));
            ImmutableArray<ISessionClient> clients = [good, broken, partial];
            var first = CaptureWith(root, clients);
            var reports = first.Manifest.Families;
            Check(first.Reparsed.SequenceEqual(["good", "broken", "partial"]) && reports["good"] is { Ok: true, Error: "", Fingerprint.Length: 64 }
                                                                              && reports["good"].Stats["sessions"] == 1, "parses every family once");
            Check(reports["broken"] is { Ok: false, Error: "InvalidOperationException: boom", Fingerprint: null, Stats.Count: 0 }, "records a failed family as Type: Message");
            Check(reports["partial"] is { Ok: false, Error: var error } && error.StartsWith("NullReferenceException: ", StringComparison.Ordinal),
                "fails a family whose document cannot render");
            Check(first.Manifest.Documents.Values.Select(meta => meta.SessionId).Order(StringComparer.Ordinal).SequenceEqual(["good-session", "partial-1"]),
                "keeps the documents rendered before a failure");
            var second = CaptureWith(root, clients);
            Check(second.Reparsed.SequenceEqual(["broken", "partial"]) && second.Manifest.Families["good"].Fingerprint == reports["good"].Fingerprint,
                "re-reads failed families but not an unchanged one");
            Check(second is { Changed: false, Manifest.DocumentCount: 2 } && second.Manifest.Families["broken"].Error == "InvalidOperationException: boom",
                "keeps the corpus while families keep failing");
            Check(CaptureWith(root, []) is { Changed: false, Reparsed.IsEmpty: true, Manifest.DocumentCount: 2 }, "keeps the corpus when no family is re-read");
            var injected = CaptureWith(root, [.. clients, new FakeClient()]);
            Check(injected.Reparsed.SequenceEqual(["broken", "partial", "fake"]) && injected.Manifest.Families.Keys.SequenceEqual(["good", "broken", "partial", "fake"])
                                                                                  && injected.Manifest.Documents.Values.Any(meta => meta.SessionId == "fake-session"),
                "captures any registered ISessionClient, in registration order");
        }

        private static Document CorpusDocument(string family, string sessionId, string text) =>
            new(family, "refactor-test", sessionId, $"{sessionId}.jsonl", [new Message("m1", "user", text, 0, "2026-10-26T00:00:00Z")]);
    }
}
