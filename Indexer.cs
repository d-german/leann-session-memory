using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SessionMemory
{
    sealed internal class TierState
    {
        public string IndexName { get; set; } = "";

        public string BuildId { get; set; } = "";

        public string PublishedAt { get; set; } = "";

        public string CorpusHash { get; set; } = "";

        public string Signature { get; set; } = "";

        public double DurationSeconds { get; set; }

        public Dictionary<string, string> Documents { get; set; } = [];
    }

    sealed internal class StagedBuild
    {
        public StagedBuild(string tier, string buildRoot, Dictionary<string, string> documents, string corpusHash)
        {
            Tier = tier;
            BuildRoot = buildRoot;
            Documents = documents;
            CorpusHash = corpusHash;
        }

        public string Tier { get; }

        public string BuildRoot { get; }

        public Dictionary<string, string> Documents { get; }

        public string CorpusHash { get; }
    }

    /// <summary>Builds, publishes, and searches the recent and archive LEANN indexes.</summary>
    internal static class Indexer
    {
        public const string CanaryId = "LEANN-MEMORY-CANARY-20261021";
        public static readonly string[] Tiers = ["recent", "archive"];
        private const string Model = "facebook/contriever";
        // A long session matches many queries; the cap leaves room for other sessions.
        private const int MaxHitsPerSession = 3;
        private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // A private LEANN .NET installed by Install-SessionMemory.ps1; 2.10+ ranks several indexes together.
        public static readonly string Leann = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeannSessionMemory", "leann", "leann-dotnet.exe");
        private static readonly string ModelFile = Path.Combine(Home, ".leann", "models", "facebook-contriever", "model.onnx");
        private static readonly JsonSerializerOptions Output = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string IndexName(string tier) => tier == "archive" ? "session-memory" : "session-memory-recent";

        public static bool Exists(string root, string tier) => Directory.Exists(Path.Combine(root, ".leann", "indexes", IndexName(tier)));

        public static string StatePath(string root, string tier) => Path.Combine(root, "state", $"{tier}-state.json");

        public static TierState LoadState(string root, string tier) => Store.Load<TierState>(StatePath(root, tier)) ?? new TierState();

        // Sessions whose current text differs from the archive, plus the canary that validates every build.
        public static Dictionary<string, string> RecentDocuments(IReadOnlyDictionary<string, DocumentMeta> documents, IReadOnlyDictionary<string, string> archived) =>
            documents
                .Where(pair => !archived.TryGetValue(pair.Key, out var sha) || sha != pair.Value.Sha256 || pair.Value.SessionId == CanaryId)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Sha256);

        public static string Signature(IReadOnlyDictionary<string, string> documents) =>
            Transcript.Sha256(string.Concat(documents.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}\t{pair.Value}\n")));

        // Called between captures so copying never races a corpus rewrite.
        public static StagedBuild Stage(string root, string tier, IReadOnlyDictionary<string, string> documents, string corpusHash)
        {
            var builds = Path.Combine(root, "builds", IndexName(tier));
            DeleteDirectory(builds);
            var buildRoot = Path.Combine(builds, $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Guid.NewGuid().ToString("N")[..8]}");
            var docs = Path.Combine(buildRoot, "docs");
            Directory.CreateDirectory(docs);
            var copied = new Dictionary<string, string>();
            foreach (var (name, sha) in documents)
            {
                var source = Path.Combine(root, "corpus", name);
                if (!File.Exists(source)) continue;
                File.Copy(source, Path.Combine(docs, name));
                copied[name] = sha;
            }
            return new StagedBuild(tier, buildRoot, copied, corpusHash);
        }

        public static TierState Build(string root, StagedBuild staged)
        {
            if (!File.Exists(Leann) || !File.Exists(ModelFile)) throw new FileNotFoundException("LEANN .NET or the facebook/contriever model is missing.");
            var stopwatch = Stopwatch.StartNew();
            var index = IndexName(staged.Tier);
            var logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            var docs = Path.Combine(staged.BuildRoot, "docs");
            try
            {
                string[] arguments =
                [
                    "--rebuild", "--docs", docs, "--index-name", index, "--model", Model, "--file-types", ".md",
                    "--chunk-size", "1400", "--chunk-overlap", "250", "--max-tokens", "512", "--batch-size", "16"
                ];
                var environment = new Dictionary<string, string>
                {
                    ["LEANN_DATA_ROOT"] = staged.BuildRoot,
                    ["LEANN_MODEL"] = Model
                };
                var (exitCode, output, error) = RunLeann(arguments, environment, docs);
                File.WriteAllText(Path.Combine(logs, $"{index}-build.log"), output + error);
                if (exitCode != 0) throw new InvalidOperationException($"LEANN rebuild exited with {exitCode}; see {logs}");
                Validate(staged.BuildRoot, index, Path.Combine(logs, $"{index}-validate.log"), "Candidate");
                Publish(root, Path.Combine(staged.BuildRoot, ".leann", "indexes", index), index);
                Validate(root, index, Path.Combine(logs, $"{index}-published.log"), "Published");
            }
            finally
            {
                DeleteDirectory(Path.GetDirectoryName(staged.BuildRoot)!);
            }
            return new TierState
            {
                IndexName = index,
                BuildId = Path.GetFileName(staged.BuildRoot),
                PublishedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture),
                CorpusHash = staged.CorpusHash,
                Signature = Signature(staged.Documents),
                DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
                Documents = staged.Documents
            };
        }

        // One LEANN query ranks both indexes together. Each index's own scores are rank positions and cannot be
        // compared, so merging two separate searches let the small recent index outrank better archive matches.
        public static int Search(string root, string query, int topK, bool json, bool includeCurrent = false, string? onlySession = null)
        {
            var tiers = Tiers.Where(tier => Exists(root, tier)).ToList();
            if (tiers.Count == 0)
            {
                Console.Error.WriteLine("No session-memory index has been built yet.");
                return 1;
            }
            // Over-fetch, because stale archive copies and hits past the per-session cap are dropped below.
            var (exitCode, output, error, source) = Query(root, string.Join(',', tiers.Select(IndexName)), query, Math.Min(topK * 5, 60));
            JsonArray found;
            try
            {
                found = JsonNode.Parse(output)?["results"] as JsonArray ?? throw new JsonException();
            }
            catch (JsonException)
            {
                Console.Error.WriteLine($"LEANN search failed with exit code {exitCode}: {error}");
                return 1;
            }
            var recent = LoadState(root, "recent").Documents.Keys.ToHashSet();
            var documents = Store.Load<Manifest>(Path.Combine(root, "state", "manifest.json"))?.Documents ?? [];
            // Exclusions apply before the next capture removes the session from the corpus.
            var exclusions = Corpus.SearchExclusions(root, includeCurrent);
            var current = Visible(documents, exclusions, onlySession);
            var ranked = found.OfType<JsonObject>().Select(hit => hit.DeepClone().AsObject());
            // An empty set keeps every hit when there is no manifest, but a session with no indexed document has none.
            var hits = onlySession is not null && current.Count == 0 ? [] : Filter(ranked, tiers, recent, current, topK);
            if (json) WriteJson(root, query, source, hits, documents);
            else WriteText(source, hits, documents);
            return 0;
        }

        // The documents a search may show; onlySession narrows them to one session and its subagents.
        public static HashSet<string> Visible(IReadOnlyDictionary<string, DocumentMeta> documents, IReadOnlyCollection<string> exclusions, string? onlySession) =>
        [
            .. documents
                .Where(pair => !Corpus.IsExcluded(pair.Value.SessionId, exclusions)
                               && (onlySession is null || pair.Value.SessionId.StartsWith(onlySession, StringComparison.OrdinalIgnoreCase)))
                .Select(pair => pair.Key)
        ];

        // Labels each hit with its tier and keeps LEANN's order. A one-index search carries no index name.
        public static List<JsonObject> Filter(IEnumerable<JsonObject> ranked, IReadOnlyList<string> tiers, IReadOnlySet<string> recent, IReadOnlySet<string> current, int topK)
        {
            var perSession = new Dictionary<string, int>();
            return
            [
                .. ranked
                    .Select(hit =>
                    {
                        hit["tier"] = Tiers.FirstOrDefault(tier => IndexName(tier) == hit["index"].AsString()) ?? tiers[0];
                        return hit;
                    })
                    .Where(hit => Keep(hit["tier"].AsString()!, hit["file"].AsString() ?? "", recent, current))
                    .Where(hit =>
                    {
                        var file = hit["file"].AsString() ?? "";
                        perSession[file] = perSession.GetValueOrDefault(file) + 1;
                        return perSession[file] <= MaxHitsPerSession;
                    })
                    .Take(topK)
            ];
        }

        public static void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }

        // Hits are dropped when the session is gone or excluded, and archive hits when the recent index holds the newer copy.
        private static bool Keep(string tier, string file, IReadOnlySet<string> recent, IReadOnlySet<string> current) =>
            (current.Count == 0 || current.Contains(file)) && (tier != "archive" || !recent.Contains(file));

        // One header line per session, then its matching messages without the per-message transcript headers.
        private static void WriteText(string source, List<JsonObject> hits, Dictionary<string, DocumentMeta> documents)
        {
            var text = new StringBuilder();
            if (source != "server") text.Append("(the search server is not running, so this search took the slow path)\n");
            foreach (var session in hits.GroupBy(hit => hit["file"].AsString() ?? ""))
            {
                var meta = documents.GetValueOrDefault(session.Key);
                text.Append(Compact.SessionLine(meta?.SessionId ?? session.Key, meta?.Client, $"last {Compact.Time(meta?.LastActivity ?? "")}", meta?.Title)).Append('\n');
                foreach (var segment in session.SelectMany(hit => Compact.Segments(hit["text"].AsString() ?? "")))
                    text.Append(Compact.Label(segment)).Append(' ').Append(segment.Text).Append('\n');
            }
            Compact.Write(Compact.Plain(text.ToString()));
        }

        private static void WriteJson(string root, string query, string source, List<JsonObject> hits, Dictionary<string, DocumentMeta> documents)
        {
            foreach (var hit in hits)
            {
                // LEANN's metadata dates are the corpus file's, not the conversation's.
                hit.Remove("metadata");
                if (!documents.TryGetValue(hit["file"].AsString() ?? "", out var meta)) continue;
                hit["title"] = meta.Title;
                hit["client"] = meta.Client;
                hit["session_id"] = meta.SessionId;
                hit["last_activity"] = meta.LastActivity;
                hit["transcript"] = Path.Combine(root, "corpus", hit["file"]!.GetValue<string>());
            }
            var response = new JsonObject
            {
                ["query"] = query,
                ["count"] = hits.Count,
                ["served_by"] = source,
                ["results"] = new JsonArray([.. hits])
            };
            Console.Out.WriteLine(Ascii(response.ToJsonString(Output)));
        }

        private static void Validate(string dataRoot, string index, string log, string label)
        {
            var (exitCode, output, error) = RunLeann(SearchArguments(dataRoot, index, CanaryId, 8));
            File.WriteAllText(log, output + error);
            if (exitCode != 0 || !output.Contains(CanaryId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{label} index failed canary validation; see {log}");
        }

        private static void Publish(string root, string candidate, string index)
        {
            var indexes = Path.Combine(root, ".leann", "indexes");
            Directory.CreateDirectory(indexes);
            var destination = Path.Combine(indexes, index);
            var previous = Path.Combine(indexes, $".{index}-previous");
            DeleteDirectory(previous);
            Store.Retry(() =>
            {
                if (Directory.Exists(destination)) Directory.Move(destination, previous);
            });
            try
            {
                Store.Retry(() => Directory.Move(candidate, destination));
            }
            catch
            {
                if (Directory.Exists(previous) && !Directory.Exists(destination)) Directory.Move(previous, destination);
                throw;
            }
            DeleteDirectory(previous);
        }

        // Near-duplicate filtering stays off: an unchanged passage exists in both indexes, and if LEANN kept the
        // archive copy, dropping stale archive hits would lose it.
        private static string[] SearchArguments(string dataRoot, string index, string query, int topK) =>
        [
            "--search", "--data-root", dataRoot, "--index", index, "--query", query, "--top-k", topK.ToString(CultureInfo.InvariantCulture),
            "--dedup-threshold", "0", "--show-metadata", "--json"
        ];

        private static (int ExitCode, string Output, string Error, string Source) Query(string root, string index, string query, int topK)
        {
            if (SearchServer.Search(root, index, query, topK) is { } json) return (0, json, "", "server");
            var (exitCode, output, error) = RunLeann(SearchArguments(root, index, query, topK));
            return (exitCode, output, error, "cli");
        }

        private static (int ExitCode, string Output, string Error) RunLeann(IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null)
        {
            var start = new ProcessStartInfo(Leann)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workingDirectory ?? ""
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start LEANN .NET.");
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output, error.GetAwaiter().GetResult());
        }

        // ASCII output survives any console code page.
        internal static string Ascii(string json)
        {
            var builder = new StringBuilder(json.Length);
            foreach (var character in json)
            {
                if (character <= 0x7F) builder.Append(character);
                else builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }
    }
}
