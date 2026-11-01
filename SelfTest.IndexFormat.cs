using CSharpFunctionalExtensions;
using Microsoft.Extensions.DependencyInjection;

namespace SessionMemory
{
    internal static partial class SelfTest
    {
        private static void IndexFormatChecks()
        {
            var canaryDocuments = new Dictionary<string, DocumentMeta>
            {
                ["probe.md"] = new() { SessionId = Indexer.CanaryId, Sha256 = new string('a', 64) },
                ["ordinary.md"] = new() { SessionId = "ordinary" }
            };
            Check(Indexer.CanaryFound(new LeannOutput(0, "{\"results\":[{\"file\":\"probe.md__h" + new string('a', 24) + "__m000001_p001.md\"}]}", ""), canaryDocuments)
                  && !Indexer.CanaryFound(new LeannOutput(0, "{\"results\":[{\"file\":\"ordinary.md\",\"text\":\"" + Indexer.CanaryId + "\"}]}", ""), canaryDocuments),
                "validates the actual canary source instead of a mention of its identifier");
            var temporary = Directory.CreateTempSubdirectory("session-memory-format-");
            using var cleanup = new TemporaryDirectory(temporary.FullName);
            var root = temporary.FullName;
            var indexes = Path.Combine(root, ".leann", "indexes");
            var legacyArchive = Path.Combine(indexes, Indexer.IndexName("archive", false));
            var legacyRecent = Path.Combine(indexes, Indexer.IndexName("recent", false));
            Directory.CreateDirectory(legacyArchive);
            Directory.CreateDirectory(legacyRecent);
            File.WriteAllText(Path.Combine(legacyArchive, "sentinel"), "legacy");
            var legacyState = Path.Combine(root, "state", Indexer.StateFileName("archive", false));
            Store.Save(legacyState, new TierState { IndexName = "legacy", Documents = new Dictionary<string, string> { ["old.md"] = "old" } });
            var before = File.ReadAllText(legacyState);
            var exclusionFile = Path.Combine(root, "state", "excluded.json");
            Store.Save(exclusionFile, new[] { "excluded-test-id" });
            var exclusionsBefore = File.ReadAllText(exclusionFile);

            using var services = Services(Path.Combine(root, "home"), root);
            var indexer = services.GetRequiredService<Indexer>();
            var status = services.GetRequiredService<StatusReport>();
            Check(!Indexer.ActiveCurrentFormat(root) && indexer.SearchTiers(false).SequenceEqual(["recent", "archive"])
                  && status.Read()["archive-state"]?["index_name"].AsString() == "legacy",
                "search and status use the legacy group before a current index exists");
            Check(!indexer.Exists("archive") && indexer.LoadState("archive").Documents.Count == 0,
                "legacy publication does not satisfy a current-format build");

            var corpus = Path.Combine(root, "corpus");
            Directory.CreateDirectory(corpus);
            const string name = "example.md";
            const string original = "## Message 000001 part 1/1\nrole: user\ntimestamp: 2026-01-01T00:00:00Z\ntext:\nExplain the job.\n"
                                    + "## Message 000002 part 1/1\nrole: assistant\ntimestamp: 2026-01-01T00:01:00Z\ntext:\nThe job completed.\n";
            var hash = Transcript.Sha256(original);
            var docs = new Dictionary<string, DocumentMeta> { [name] = new() { SessionId = "example", Title = "Example title", Sha256 = hash } };
            Store.Save(Path.Combine(root, "state", "manifest.json"), new Manifest { Documents = docs });
            File.WriteAllText(Path.Combine(corpus, name), original);
            var staged = indexer.Stage("archive", new Dictionary<string, string> { [name] = hash }, "corpus-hash");
            var passages = Directory.GetFiles(Path.Combine(staged.BuildRoot, "docs"));
            Check(staged.Documents.Count == 1 && staged.Documents[name] == hash && passages.Length == 2
                  && passages.All(file => File.ReadAllText(file).Length < 1400
                      && SemanticPassages.TrySourceFile(Path.GetFileName(file), docs, out var source, out var number)
                      && source == name && number is 1 or 2)
                  && passages.Any(file => File.ReadAllText(file).Contains("Example title")),
                "stages bounded decodable passages while tracking original document hashes");
            var savedPassage = File.ReadAllText(passages[0]);
            File.WriteAllText(Path.Combine(corpus, name), original.Replace("\n", "\r\n"), new System.Text.UTF8Encoding(true));
            var windowsSnapshot = indexer.Stage("recent", new Dictionary<string, string> { [name] = hash }, "corpus-hash");
            Check(Directory.GetFiles(Path.Combine(windowsSnapshot.BuildRoot, "docs")).Length == 2,
                "accepts equivalent Windows line endings and UTF-8 byte-order marks against canonical hashes");
            File.WriteAllText(Path.Combine(corpus, name), original + "changed");
            Check(File.ReadAllText(passages[0]) == savedPassage, "staged passages are independent of later source writes");
            docs[name] = docs[name] with { Sha256 = Transcript.Sha256(original + "changed") };
            Check(!SemanticPassages.TrySourceFile(Path.GetFileName(passages[0]), docs, out _), "changed sources reject stale passages");
            var failed = false;
            try { indexer.Stage("recent", new Dictionary<string, string> { [name] = hash }, "corpus-hash"); }
            catch (InvalidDataException) { failed = true; }
            Check(failed, "staging rejects a source whose hash differs from the captured manifest");

            var currentIndex = Path.Combine(indexes, Indexer.IndexName("archive"));
            var candidate = Path.Combine(root, "candidate");
            Directory.CreateDirectory(candidate);
            File.WriteAllText(Path.Combine(candidate, "sentinel"), "candidate");
            var rejected = Indexer.Publish(root, candidate, Indexer.IndexName("archive"), () => UnitResult.Failure("rejected"));
            Check(rejected.IsFailure && !Indexer.ActiveCurrentFormat(root) && File.Exists(Path.Combine(legacyArchive, "sentinel")),
                "a failed first publication removes the candidate and keeps legacy search active");
            Directory.CreateDirectory(currentIndex);
            File.WriteAllText(Path.Combine(currentIndex, "sentinel"), "previous");
            Directory.CreateDirectory(candidate);
            var rejectedReplacement = Indexer.Publish(root, candidate, Indexer.IndexName("archive"), () => UnitResult.Failure("rejected"));
            Check(rejectedReplacement.IsFailure && File.ReadAllText(Path.Combine(currentIndex, "sentinel")) == "previous",
                "a failed published validation restores the previous index");
            Directory.CreateDirectory(candidate);
            File.WriteAllText(Path.Combine(candidate, "sentinel"), "accepted");
            var accepted = Indexer.Publish(root, candidate, Indexer.IndexName("archive"), () => UnitResult.Success<string>());
            Check(accepted.IsSuccess && File.ReadAllText(Path.Combine(currentIndex, "sentinel")) == "accepted"
                  && !Directory.Exists(Path.Combine(indexes, $".{Indexer.IndexName("archive")}-previous")),
                "a validated publication replaces the prior index and removes its backup");
            Check(Indexer.ActiveCurrentFormat(root) && indexer.SearchTiers(true).SequenceEqual(["archive"])
                  && status.Read()["archive-state"] is null,
                "the current group excludes legacy recent and reads only its own state");
            var legacyHit = new System.Text.Json.Nodes.JsonObject { ["file"] = "example.md", ["index"] = Indexer.IndexName("recent", false) };
            var currentHit = new System.Text.Json.Nodes.JsonObject { ["file"] = "example.md", ["index"] = Indexer.IndexName("archive") };
            var tiers = Indexer.Filter([legacyHit, currentHit], ["recent", "archive"], new HashSet<string>(), new HashSet<string> { name }, 8);
            Check(tiers[0]["tier"].AsString() == "recent" && tiers[1]["tier"].AsString() == "archive",
                "labels legacy and current index names by their actual tiers");
            Store.Save(indexer.StatePath("archive"), new TierState { IndexName = "current" });
            Check(status.Read()["archive-state"]?["index_name"].AsString() == "current"
                  && File.ReadAllText(legacyState) == before
                  && File.ReadAllText(Path.Combine(legacyArchive, "sentinel")) == "legacy"
                  && File.ReadAllText(exclusionFile) == exclusionsBefore,
                "current staging and state leave legacy data and exclusions intact");
        }
    }
}
