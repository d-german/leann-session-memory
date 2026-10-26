using System.Collections.Immutable;
using System.Text.Json;
using static SessionMemory.ParserHelpers;

namespace SessionMemory.Clients.Synthetic
{
    /// <summary>
    /// The synthetic proof conversations under the tool's root, including the canary every index build must find. These are plain
    /// functions: ClientRegistration turns them into a client with LambdaSessionClient instead of a class of their own.
    /// </summary>
    internal static class SyntheticTranscripts
    {
        public const string FamilyName = "synthetic";

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

        public static ImmutableArray<string> Files(string root)
        {
            var directory = Path.Combine(root, "synthetic");
            return Directory.Exists(directory) ? [.. Directory.EnumerateFiles(directory, "*.jsonl")] : [];
        }

        public static FamilyResult Parse(string root)
        {
            var grouped = new Dictionary<string, List<Message>>();
            var sources = new Dictionary<string, string>();
            var stats = Stats("files", "sessions", "malformed", "partial_trailing");
            foreach (var path in Files(root))
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
            ImmutableArray<Document> documents = [.. grouped.Select(pair => new Document(FamilyName, "synthetic-proof", pair.Key, sources[pair.Key], [.. pair.Value]))];
            stats["sessions"] = documents.Length;
            return new FamilyResult(documents, stats);
        }
    }
}
