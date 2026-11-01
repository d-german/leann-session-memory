using System.Collections.Frozen;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpFunctionalExtensions;
using SessionMemory.Clients;

namespace SessionMemory
{
    sealed internal record TierState
    {
        public string IndexName { get; init; } = "";

        public string BuildId { get; init; } = "";

        public string PublishedAt { get; init; } = "";

        public string CorpusHash { get; init; } = "";

        public string Signature { get; init; } = "";

        public double DurationSeconds { get; init; }

        public Dictionary<string, string> Documents { get; init; } = [];
    }

    sealed internal record StagedBuild
    {
        public StagedBuild(string tier, string buildRoot, Dictionary<string, string> documents, string corpusHash)
        {
            Tier = tier;
            BuildRoot = buildRoot;
            Documents = documents;
            CorpusHash = corpusHash;
        }

        public string Tier { get; init; }

        public string BuildRoot { get; init; }

        public Dictionary<string, string> Documents { get; init; }

        public string CorpusHash { get; init; }
    }

    /// <summary>One search command: the query and the options that shape its results.</summary>
    sealed internal record SearchRequest
    {
        public required string Query { get; init; }

        public int TopK { get; init; } = 8;

        public bool Latest { get; init; }

        public bool Json { get; init; }

        public bool IncludeCurrent { get; init; }

        // Narrows the search to one session and its subagents.
        public string? OnlySession { get; init; }

        public string? ExcludedTopic { get; init; }
    }

    /// <summary>What one LEANN .NET process exited with and wrote.</summary>
    sealed internal record LeannOutput
    {
        public LeannOutput(int exitCode, string output, string error)
        {
            ExitCode = exitCode;
            Output = output;
            Error = error;
        }

        public int ExitCode { get; init; }

        public string Output { get; init; }

        public string Error { get; init; }
    }

    /// <summary>
    /// The private LEANN .NET that Install-SessionMemory.ps1 installs (2.10+ ranks several indexes together) and the embedding model it uses.
    /// </summary>
    sealed internal class LeannInstallation
    {
        public LeannInstallation(ClientContext paths)
        {
            Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeannSessionMemory", "leann",
                OperatingSystem.IsWindows() ? "leann-dotnet.exe" : "leann-dotnet");
            Model = Path.Combine(paths.Home, ".leann", "models", "facebook-contriever", "model.onnx");
        }

        public string Executable { get; }

        public string Model { get; }
    }

    /// <summary>
    /// A folder deleted when the scope that holds it ends: normally, by a failed result or by an exception. Deleting is best effort, so a folder
    /// another process still holds open stays behind instead of failing the caller.
    /// </summary>
    sealed internal class TemporaryDirectory : IDisposable
    {
        private readonly string _path;

        public TemporaryDirectory(string path)
        {
            _path = path;
        }

        public void Dispose() => Indexer.DeleteDirectory(_path);
    }

    /// <summary>Builds, publishes, and searches the recent and archive LEANN indexes.</summary>
    sealed internal class Indexer
    {
        public const string CanaryId = "LEANN-MEMORY-CANARY-20261021";
        public static readonly ImmutableArray<string> Tiers = ["recent", "archive"];
        private const string Model = "facebook/contriever";
        // A long session matches many queries; the cap leaves room for other sessions.
        private const int MaxHitsPerSession = 3;
        private static readonly JsonSerializerOptions Output = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly string _root;
        private readonly LeannInstallation _leann;
        private readonly ExclusionService _exclusions;
        private readonly SearchServer _searchServer;

        public Indexer(ClientContext paths, LeannInstallation leann, ExclusionService exclusions, SearchServer searchServer)
        {
            _root = paths.Root;
            _leann = leann;
            _exclusions = exclusions;
            _searchServer = searchServer;
        }

        public static string IndexName(string tier) => IndexName(tier, true);

        public static string IndexName(string tier, bool current) => current
            ? tier == "archive" ? "session-memory-conversations-v1" : "session-memory-conversations-v1-recent"
            : tier == "archive" ? "session-memory" : "session-memory-recent";

        public static string StateFileName(string tier, bool current) => current ? $"{tier}-conversations-v1-state.json" : $"{tier}-state.json";

        public static bool ActiveCurrentFormat(string root) =>
            Tiers.Any(tier => Directory.Exists(Path.Combine(root, ".leann", "indexes", IndexName(tier))));

        public ImmutableArray<string> SearchTiers(bool current) =>
            [.. Tiers.Where(tier => Directory.Exists(Path.Combine(_root, ".leann", "indexes", IndexName(tier, current))))];

        public bool Exists(string tier) => Directory.Exists(Path.Combine(_root, ".leann", "indexes", IndexName(tier)));

        public string StatePath(string tier) => Path.Combine(_root, "state", StateFileName(tier, true));

        public TierState LoadState(string tier) => Store.Load<TierState>(StatePath(tier)) ?? new TierState();

        private TierState LoadSearchState(string tier, bool current) =>
            Store.Load<TierState>(Path.Combine(_root, "state", StateFileName(tier, current))) ?? new TierState();

        // Sessions whose current text differs from the archive, plus the canary that validates every build.
        public static Dictionary<string, string> RecentDocuments(IReadOnlyDictionary<string, DocumentMeta> documents, IReadOnlyDictionary<string, string> archived) =>
            documents
                .Where(pair => !archived.TryGetValue(pair.Key, out var sha) || sha != pair.Value.Sha256 || pair.Value.SessionId == CanaryId)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Sha256);

        public static string Signature(IReadOnlyDictionary<string, string> documents) =>
            Transcript.Sha256(string.Concat(documents.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}\t{pair.Value}\n")));

        // Called between captures so copying never races a corpus rewrite.
        public StagedBuild Stage(string tier, IReadOnlyDictionary<string, string> documents, string corpusHash)
        {
            var builds = Path.Combine(_root, "builds", IndexName(tier));
            DeleteDirectory(builds);
            var buildRoot = Path.Combine(builds, $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Guid.NewGuid().ToString("N")[..8]}");
            var docs = Path.Combine(buildRoot, "docs");
            Directory.CreateDirectory(docs);
            var copied = new Dictionary<string, string>();
            var manifest = Store.Load<Manifest>(Path.Combine(_root, "state", "manifest.json"));
            foreach (var (name, sha) in documents)
            {
                if (name != Path.GetFileName(name) || name.IndexOfAny(['/', '\\', ':']) >= 0)
                    throw new InvalidDataException("Invalid corpus filename.");
                var source = Path.Combine(_root, "corpus", name);
                if (!File.Exists(source)) continue;
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Corpus source is a filesystem link.");
                var content = File.ReadAllText(source).Replace("\r\n", "\n", StringComparison.Ordinal);
                if (!string.Equals(Transcript.Sha256(content), sha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Corpus source changed during staging.");
                var snapshot = Path.Combine(buildRoot, "source-snapshot.md");
                File.WriteAllText(snapshot, content, new UTF8Encoding(false));
                try
                {
                    var title = manifest?.Documents.GetValueOrDefault(name)?.Title ?? "";
                    if (SemanticPassages.Write(snapshot, docs, name, title) == 0 && content.Length > 0)
                        throw new InvalidDataException("A nonempty corpus source produced no semantic passages.");
                }
                finally { File.Delete(snapshot); }
                copied[name] = sha;
            }
            return new StagedBuild(tier, buildRoot, copied, corpusHash);
        }

        // Failures read "{ExceptionType}: {Message}", the text the watcher has always persisted for a failed build.
        public Result<TierState, string> Build(StagedBuild staged, IndexProgress progress)
        {
            if (!File.Exists(_leann.Executable) || !File.Exists(_leann.Model))
                return Result.Failure<TierState, string>($"{nameof(FileNotFoundException)}: LEANN .NET or the facebook/contriever model is missing.");
            var stopwatch = Stopwatch.StartNew();
            var index = IndexName(staged.Tier);
            var logs = Path.Combine(_root, "logs");
            Directory.CreateDirectory(logs);
            return RebuildAndPublish(staged, index, logs, progress).Map(() => new TierState
            {
                IndexName = index,
                BuildId = Path.GetFileName(staged.BuildRoot),
                PublishedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture),
                CorpusHash = staged.CorpusHash,
                Signature = Signature(staged.Documents),
                DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
                Documents = staged.Documents
            });
        }

        // The build folder is deleted before the state is recorded, however the build ends: published, failed, or by an exception.
        private UnitResult<string> RebuildAndPublish(StagedBuild staged, string index, string logs, IndexProgress progress)
        {
            using var workspace = new TemporaryDirectory(Path.GetDirectoryName(staged.BuildRoot)!);
            return Rebuild(staged.BuildRoot, index, logs, progress)
                .Tap(() => progress.Report("validating"))
                .Bind(() => Validate(staged.BuildRoot, index, Path.Combine(logs, $"{index}-validate.log"), "Candidate"))
                .Tap(() => progress.Report("publishing"))
                .Bind(() => Publish(_root, Path.Combine(staged.BuildRoot, ".leann", "indexes", index), index, () =>
                {
                    progress.Report("validating");
                    return Validate(_root, index, Path.Combine(logs, $"{index}-published.log"), "Published");
                }));
        }

        // One LEANN query ranks both indexes together. Each index's own scores are rank positions and cannot be
        // compared, so merging two separate searches let the small recent index outrank better archive matches.
        // Returns the compact text or, for --json, the ASCII JSON.
        public Result<string, CliError> Search(SearchRequest request)
        {
            var currentFormat = ActiveCurrentFormat(_root);
            var tiers = SearchTiers(currentFormat);
            if (tiers.IsEmpty) return new CliError("No session-memory index has been built yet.", 1);
            // Over-fetch, because stale archive copies and hits past the per-session cap are dropped below.
            return Query(string.Join(',', tiers.Select(tier => IndexName(tier, currentFormat))), request.Query, Math.Clamp(request.TopK * 10, 80, 200))
                .Match(found => RenderChecked(request, tiers, currentFormat, found.Hits, found.Source),
                    _ => RenderChecked(request, tiers, currentFormat, [], "lexical"));
        }

        private Result<string, CliError> RenderChecked(SearchRequest request, ImmutableArray<string> tiers, bool currentFormat, JsonArray found, string source) =>
            Attempt.Json(() => Attempt.Io(() => Render(request, tiers, currentFormat, found, source))).Bind(result => result)
                .MapError(error => new CliError(error, 1));

        // LEANN's ranked hits. Output that is not JSON or holds no results array fails with the exit code and standard error.
        internal static Result<JsonArray, string> Hits(LeannOutput output) =>
            Attempt.Json(() => JsonNode.Parse(output.Output)?["results"] as JsonArray)
                .GetValueOrDefault()
                .ToResult($"LEANN search failed with exit code {output.ExitCode}: {output.Error}");

        private string Render(SearchRequest request, ImmutableArray<string> tiers, bool currentFormat, JsonArray found, string source)
        {
            var recent = tiers.Contains("recent") ? LoadSearchState("recent", currentFormat).Documents.Keys.ToFrozenSet() : FrozenSet<string>.Empty;
            var documents = Store.Load<Manifest>(Path.Combine(_root, "state", "manifest.json"))?.Documents ?? [];
            // Exclusions apply before the next capture removes the session from the corpus.
            var storedExclusions = _exclusions.LoadChecked();
            var exclusions = _exclusions.ForSearch(request.IncludeCurrent, stored: storedExclusions);
            var current = Visible(documents, exclusions, request.OnlySession);
            var indexable = Visible(documents, [.. storedExclusions], null);
            var ranked = found.OfType<JsonObject>().Select(hit => hit.DeepClone().AsObject())
                .Where(hit => ResolvePassage(hit, documents));
            // An empty set keeps every hit when there is no manifest, but a session with no indexed document has none.
            var candidates = Filter(ranked, tiers, recent, current, 200);
            var hits = ConversationSearch.Retrieve(_root, request.Query, documents, current, candidates, request.TopK, request.Latest, indexable, request.ExcludedTopic);
            return request.Json ? RenderJson(request.Query, source, hits, documents) : RenderText(source, hits, documents);
        }

        private static bool ResolvePassage(JsonObject hit, IReadOnlyDictionary<string, DocumentMeta> documents)
        {
            var indexedFile = hit["file"].AsString() ?? "";
            if (!SemanticPassages.TrySourceFile(indexedFile, documents, out var source)) return false;
            hit["file"] = source;
            if (indexedFile != source) hit["semantic_passage"] = indexedFile;
            return true;
        }

        // The documents a search may show; onlySession narrows them to one session and its subagents.
        public static FrozenSet<string> Visible(IReadOnlyDictionary<string, DocumentMeta> documents, ImmutableArray<string> exclusions, string? onlySession) =>
            documents
                .Where(pair => !ExclusionService.IsExcluded(pair.Value.SessionId, exclusions)
                               && (onlySession is null || pair.Value.SessionId.StartsWith(onlySession, StringComparison.OrdinalIgnoreCase)))
                .Select(pair => pair.Key)
                .ToFrozenSet();

        // Labels each hit with its tier and keeps LEANN's order. A one-index search carries no index name.
        public static ImmutableArray<JsonObject> Filter(IEnumerable<JsonObject> ranked, ImmutableArray<string> tiers, IReadOnlySet<string> recent, IReadOnlySet<string> current,
            int topK)
        {
            var perSession = new Dictionary<string, int>();
            return
            [
                .. ranked
                    .Select(hit =>
                    {
                        hit["tier"] = Tiers.FirstOrDefault(tier => IndexName(tier) == hit["index"].AsString()
                                                                      || IndexName(tier, false) == hit["index"].AsString()) ?? tiers[0];
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
        internal static string RenderText(string source, ImmutableArray<JsonObject> hits, Dictionary<string, DocumentMeta> documents)
        {
            var text = new StringBuilder();
            if (source == "lexical") text.Append("(semantic search is unavailable; showing local keyword matches)\n");
            else if (source != "server") text.Append("(the search server is not running, so this search took the slow path)\n");
            text.Append("Excerpts are not complete project status. Cite dated messages and check newer, open or pending follow-ups before claiming completion, publication or verification.\n");
            text.Append("Read original messages with show <session-id> --message <n> --context 1 --chars 6000 before answering.\n");
            foreach (var session in hits.GroupBy(hit => hit["file"].AsString() ?? ""))
            {
                var meta = documents.GetValueOrDefault(session.Key);
                text.Append(Compact.SessionLine(meta?.SessionId ?? session.Key, meta?.Client, $"last {Compact.Time(meta?.LastActivity ?? "")}", meta?.Title)).Append('\n');
                foreach (var segment in session.SelectMany(hit => Compact.Segments(hit["text"].AsString() ?? "")))
                    text.Append(Compact.Label(segment)).Append(' ').Append(segment.Text).Append('\n');
            }
            return Compact.Plain(text.ToString());
        }

        private string RenderJson(string query, string source, ImmutableArray<JsonObject> hits, Dictionary<string, DocumentMeta> documents)
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
                hit["read_context"] = new JsonObject
                {
                    ["session_id"] = meta.SessionId,
                    ["message_numbers"] = hit["evidence_messages"]?.DeepClone()
                        ?? new JsonArray(JsonValue.Create(hit["message_number"]?.GetValue<int>() ?? 0)),
                    ["context"] = 1,
                    ["chars"] = 6000
                };
                hit["transcript"] = Path.Combine(_root, "corpus", hit["file"]!.GetValue<string>());
            }
            var response = new JsonObject
            {
                ["query"] = query,
                ["count"] = hits.Length,
                ["session_count"] = hits.Select(hit => hit["file"].AsString()).Distinct(StringComparer.Ordinal).Count(),
                ["served_by"] = source,
                ["evidence_notice"] = "Selected original excerpts, not a verified project status. Check open or pending follow-ups; do not infer completion or publication.",
                ["results"] = new JsonArray([.. hits])
            };
            return Ascii(response.ToJsonString(Output));
        }

        // The log is written before the exit code is checked, so a failed build leaves LEANN's output behind.
        private UnitResult<string> Rebuild(string buildRoot, string index, string logs, IndexProgress progress)
        {
            var docs = Path.Combine(buildRoot, "docs");
            string[] arguments =
            [
                "--rebuild", "--docs", docs, "--index-name", index, "--model", Model, "--file-types", ".md",
                "--chunk-size", "1400", "--chunk-overlap", "250", "--max-tokens", "512", "--batch-size", "16"
            ];
            var environment = new Dictionary<string, string>
            {
                ["LEANN_DATA_ROOT"] = buildRoot,
                ["LEANN_MODEL"] = Model
            };
            return RunLeann(arguments, environment, docs, progress.Observe)
                .Tap(output => File.WriteAllText(Path.Combine(logs, $"{index}-build.log"), output.Output + output.Error))
                .Ensure(output => output.ExitCode == 0, output => $"{nameof(InvalidOperationException)}: LEANN rebuild exited with {output.ExitCode}; see {logs}");
        }

        private UnitResult<string> Validate(string dataRoot, string index, string log, string label) =>
            RunLeann(SearchArguments(dataRoot, index, CanaryId, 50))
                .Tap(output => File.WriteAllText(log, output.Output + output.Error))
                .Ensure(output => CanaryFound(output, Store.Load<Manifest>(Path.Combine(_root, "state", "manifest.json"))?.Documents ?? []),
                    _ => $"{nameof(InvalidOperationException)}: {label} index failed canary validation; see {log}");

        internal static bool CanaryFound(LeannOutput output, IReadOnlyDictionary<string, DocumentMeta> documents) =>
            output.ExitCode == 0 && Hits(output).GetValueOrDefault([]).OfType<JsonObject>().Any(hit =>
                SemanticPassages.TrySourceFile(hit["file"].AsString() ?? "", documents, out var source)
                && documents[source].SessionId == CanaryId);

        internal static UnitResult<string> Publish(string root, string candidate, string index, Func<UnitResult<string>> validate)
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
            var published = false;
            var accepted = false;
            try
            {
                Store.Retry(() => Directory.Move(candidate, destination));
                published = true;
                var result = validate();
                accepted = result.IsSuccess;
                return result;
            }
            finally
            {
                if (accepted) DeleteDirectory(previous);
                else
                {
                    if (published) Directory.Delete(destination, recursive: true);
                    if (Directory.Exists(previous)) Directory.Move(previous, destination);
                }
            }
        }

        // Near-duplicate filtering stays off: an unchanged passage exists in both indexes, and if LEANN kept the
        // archive copy, dropping stale archive hits would lose it.
        private static string[] SearchArguments(string dataRoot, string index, string query, int topK) =>
        [
            "--search", "--data-root", dataRoot, "--index", index, "--query", query, "--top-k", topK.ToString(CultureInfo.InvariantCulture),
            "--dedup-threshold", "0", "--show-metadata", "--json"
        ];

        // The resident server answers in a fraction of a second; without it a fresh process loads the model and indexes first.
        // A LEANN .NET that cannot start fails with the "{ExceptionType}: {Message}" text.
        private Result<(JsonArray Hits, string Source), string> Query(string index, string query, int topK) =>
            _searchServer.Search(index, query, topK) is { } json
                ? Hits(new LeannOutput(0, json, "")).Map(hits => (Hits: hits, Source: "server"))
                : RunLeann(SearchArguments(_root, index, query, topK)).Bind(Hits).Map(hits => (Hits: hits, Source: "cli"));

        private Result<LeannOutput, string> RunLeann(IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null, Action<string>? onOutput = null)
        {
            var start = new ProcessStartInfo(_leann.Executable)
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
            return Start(start).Map(process => Collect(process, onOutput));
        }

        // A missing or unrunnable executable throws Win32Exception; the failure keeps the "{ExceptionType}: {Message}" text.
        private static Result<Process, string> Start(ProcessStartInfo start)
        {
            try
            {
                return Process.Start(start).ToResult($"{nameof(InvalidOperationException)}: Could not start LEANN .NET.");
            }
            catch (Win32Exception error)
            {
                return Result.Failure<Process, string>(Attempt.Describe(error));
            }
        }

        internal static LeannOutput Collect(Process process, Action<string>? onOutput = null)
        {
            using (process)
            {
                var error = Read(process.StandardError);
                var output = Read(process.StandardOutput);
                Task.WhenAll(output, error).GetAwaiter().GetResult();
                process.WaitForExit();
                return new LeannOutput(process.ExitCode, output.Result, error.Result);
            }

            async Task<string> Read(StreamReader reader)
            {
                if (onOutput is null) return await reader.ReadToEndAsync().ConfigureAwait(false);
                var text = new StringBuilder();
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    text.AppendLine(line);
                    onOutput(line);
                }
                return text.ToString();
            }
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
