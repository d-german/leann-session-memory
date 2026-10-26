using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpFunctionalExtensions;
using SessionMemory.Clients;

namespace SessionMemory
{
    sealed internal record DocumentMeta
    {
        public string Family { get; init; } = "";

        public string Client { get; init; } = "";

        public string SessionId { get; init; } = "";

        public string SourceRef { get; init; } = "";

        public int MessageCount { get; init; }

        public string Sha256 { get; init; } = "";

        public long Bytes { get; init; }

        // The newest message time, so a search hit shows whether the session went on after that passage.
        public string LastActivity { get; init; } = "";

        public string Title { get; init; } = "";
    }

    // Loaded from the manifest, so every property has an initializer: a field missing from the file is never null.
    sealed internal record FamilyReport
    {
        public bool Ok { get; init; } = true;

        public string Error { get; init; } = "";

        public string? Fingerprint { get; init; }

        public Dictionary<string, long> Stats { get; init; } = [];
    }

    // Dictionaries keep insertion order, so the manifest's keys stay in the order they were written.
    sealed internal record Manifest
    {
        public int Version { get; init; }

        public string GeneratedAt { get; init; } = "";

        public string CorpusHash { get; init; } = "";

        public Dictionary<string, DocumentMeta> Documents { get; init; } = [];

        public Dictionary<string, FamilyReport> Families { get; init; } = [];

        public int DocumentCount { get; init; }

        public long MessageCount { get; init; }

        public long Bytes { get; init; }
    }

    sealed internal record CaptureReport
    {
        public CaptureReport(bool changed, Manifest manifest, double durationSeconds, ImmutableArray<string> reparsed)
        {
            Changed = changed;
            Manifest = manifest;
            DurationSeconds = durationSeconds;
            Reparsed = reparsed;
        }

        public bool Changed { get; init; }

        public Manifest Manifest { get; init; }

        public double DurationSeconds { get; init; }

        public ImmutableArray<string> Reparsed { get; init; }
    }

    /// <summary>Turns transcripts into the Markdown corpus LEANN indexes, rewriting only documents that changed.</summary>
    internal static class Corpus
    {
        private const int ManifestVersion = 3;
        private const int MinimumSessionIdLength = 8;
        // The variables agent hosts set to tell the commands they run which session runs them, in the registry's order of precedence.
        // Each value is the session ID this tool indexes. Hosts that set none of them exclude nothing.
        public static readonly ImmutableArray<string> CurrentSessionVariables =
            [.. ClientRegistry.SessionHosts.Select(client => client.SessionVariable).OfType<string>()];
        private static readonly UTF8Encoding NoBom = new(false);

        // Each client is a family of the manifest; the clients are read in order, so the manifest lists their families in that order.
        public static CaptureReport Capture(ClientContext context, ImmutableArray<ISessionClient> clients)
        {
            var stopwatch = Stopwatch.StartNew();
            var root = context.Root;
            var corpus = Path.Combine(root, "corpus");
            var manifestPath = Path.Combine(root, "state", "manifest.json");
            var previous = Store.Load<Manifest>(manifestPath) ?? new Manifest();
            var documents = new Dictionary<string, DocumentMeta>();
            var contents = new Dictionary<string, string>();
            var reports = new Dictionary<string, FamilyReport>();
            var reparsed = new List<string>();
            ImmutableArray<string> exclusions = [.. LoadExclusions(root)];
            foreach (var client in clients)
            {
                var fingerprint = Fingerprint(client.Sources(context), exclusions);
                if (Unchanged(previous, client.Name, fingerprint).TryGetValue(out var known))
                {
                    reports[client.Name] = known;
                    continue;
                }
                reparsed.Add(client.Name);
                reports[client.Name] = ParseFamily(client, context, fingerprint, documents, contents);
            }
            // Unchanged or failed families keep their previous documents; a fresh parse is authoritative.
            var parsed = reparsed.Where(name => reports[name].Ok).ToHashSet();
            KeepPreviousDocuments(previous.Documents, parsed, corpus, documents);
            RemoveExcluded(exclusions, documents, contents);
            WriteChangedDocuments(corpus, previous.Documents, documents, contents);
            DeleteStaleDocuments(corpus, documents);
            var manifest = new Manifest
            {
                Version = ManifestVersion,
                GeneratedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture),
                CorpusHash = CorpusHash(documents),
                Documents = documents,
                Families = reports,
                DocumentCount = documents.Count,
                MessageCount = documents.Values.Sum(document => (long)document.MessageCount),
                Bytes = documents.Values.Sum(document => document.Bytes)
            };
            var changed = manifest.CorpusHash != previous.CorpusHash;
            if (changed || reparsed.Count > 0 || previous.Version != ManifestVersion) Store.Save(manifestPath, manifest);
            return new CaptureReport(changed, manifest, Math.Round(stopwatch.Elapsed.TotalSeconds, 3), [.. reparsed]);
        }

        // A family parsed successfully from the same sources and exclusions keeps its report and documents.
        private static Maybe<FamilyReport> Unchanged(Manifest previous, string name, string fingerprint) =>
            previous.Version == ManifestVersion
                ? previous.Families.TryFind(name).Where(known => known.Ok && known.Fingerprint == fingerprint)
                : Maybe<FamilyReport>.None;

        // Documents rendered before a parse fails stay in the corpus, so they are added as they render.
        // Any failure, including one while rendering, fails the family with the "{Type}: {Message}" text the manifest persists.
        private static FamilyReport ParseFamily(ISessionClient client, ClientContext context, string fingerprint, Dictionary<string, DocumentMeta> documents,
            Dictionary<string, string> contents) =>
            Result.Try(() =>
                {
                    var result = client.Parse(context);
                    foreach (var document in result.Documents) AddDocument(document, documents, contents);
                    return result.Stats;
                }, Attempt.Describe)
                .Match(
                    stats => new FamilyReport
                    {
                        Fingerprint = fingerprint,
                        Stats = stats
                    },
                    error => new FamilyReport
                    {
                        Ok = false,
                        Error = error
                    });

        private static void AddDocument(Document document, Dictionary<string, DocumentMeta> documents, Dictionary<string, string> contents)
        {
            var (content, kept, sourceRef) = Transcript.Render(document);
            if (kept == 0) return;
            var bytes = Encoding.UTF8.GetBytes(content);
            var name = Transcript.DocumentName(document);
            contents[name] = content;
            documents[name] = new DocumentMeta
            {
                Family = document.Family,
                Client = document.Client,
                SessionId = document.SessionId,
                SourceRef = sourceRef,
                MessageCount = kept,
                Sha256 = Transcript.Sha256(bytes),
                Bytes = bytes.Length,
                LastActivity = document.Messages.Select(message => message.Timestamp).Where(time => time.Length > 0)
                    .DefaultIfEmpty("").Max(StringComparer.Ordinal)!,
                Title = Transcript.CleanTitle(document.Title)
            };
        }

        private static void KeepPreviousDocuments(Dictionary<string, DocumentMeta> previous, HashSet<string> parsed, string corpus, Dictionary<string, DocumentMeta> documents)
        {
            foreach (var (name, meta) in previous)
            {
                if (!parsed.Contains(meta.Family) && !documents.ContainsKey(name) && File.Exists(Path.Combine(corpus, name)))
                    documents[name] = meta;
            }
        }

        private static void RemoveExcluded(ImmutableArray<string> exclusions, Dictionary<string, DocumentMeta> documents, Dictionary<string, string> contents)
        {
            foreach (var name in documents.Where(pair => IsExcluded(pair.Value.SessionId, exclusions)).Select(pair => pair.Key).ToList())
            {
                documents.Remove(name);
                contents.Remove(name);
            }
        }

        private static void WriteChangedDocuments(string corpus, Dictionary<string, DocumentMeta> previous, Dictionary<string, DocumentMeta> documents, Dictionary<string, string> contents)
        {
            Directory.CreateDirectory(corpus);
            foreach (var (name, content) in contents)
            {
                var target = Path.Combine(corpus, name);
                if (previous.TryGetValue(name, out var old) && old.Sha256 == documents[name].Sha256 && File.Exists(target)) continue;
                var temporary = Path.Combine(corpus, $".{name}.{Guid.NewGuid():N}.tmp");
                // Same bytes as Python's text-mode write on Windows.
                File.WriteAllText(temporary, content.Replace("\n", "\r\n"), NoBom);
                Store.Retry(() => File.Move(temporary, target, overwrite: true));
            }
        }

        private static void DeleteStaleDocuments(string corpus, Dictionary<string, DocumentMeta> documents)
        {
            foreach (var stale in Directory.EnumerateFiles(corpus, "*.md").Where(path => !documents.ContainsKey(Path.GetFileName(path))).ToList())
                File.Delete(stale);
        }

        public static string ExclusionsPath(string root) => Path.Combine(root, "state", "excluded.json");

        public static List<string> LoadExclusions(string root) => Store.Load<List<string>>(ExclusionsPath(root)) ?? [];

        // The ID is matched by prefix, so a value too short to be safe counts as no session.
        public static string? CurrentSession() =>
            CurrentSessionVariables
                .Select(name => Environment.GetEnvironmentVariable(name)?.Trim())
                .FirstOrDefault(value => value is { Length: >= MinimumSessionIdLength });

        // The session running a search only echoes the question, so searches leave it out unless asked not to.
        // The ID is matched like any exclusion, so a short value must not drop unrelated sessions.
        public static ImmutableArray<string> SearchExclusions(string root, bool includeCurrent, string? currentSession = null)
        {
            var exclusions = LoadExclusions(root);
            currentSession ??= CurrentSession();
            if (!includeCurrent && currentSession?.Trim() is { Length: >= MinimumSessionIdLength } id) exclusions.Add(id);
            return [.. exclusions];
        }

        // A prefix also covers Claude subagent sessions, whose IDs start with the parent's.
        public static bool IsExcluded(string sessionId, ImmutableArray<string> exclusions) =>
            exclusions.Any(prefix => sessionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        private static string Fingerprint(ImmutableArray<string> files, ImmutableArray<string> exclusions)
        {
            // The build id makes a new parser re-read every family once; the exclusions make an included session come back.
            var builder = new StringBuilder().Append(typeof(Corpus).Assembly.ManifestModule.ModuleVersionId).Append('\n');
            foreach (var exclusion in exclusions) builder.Append(exclusion).Append('\n');
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                if (info.Exists) builder.Append(file).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
            return Transcript.Sha256(builder.ToString());
        }

        private static string CorpusHash(Dictionary<string, DocumentMeta> documents)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var (name, meta) in documents.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(name));
                hash.AppendData(Encoding.UTF8.GetBytes(meta.Sha256));
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }

    /// <summary>Atomic JSON state files that readers never block.</summary>
    internal static class Store
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true
        };

        // A missing, locked or corrupt state file reads as absent; callers fall back to their defaults.
        public static T? Load<T>(string path) where T : class =>
            ReadJson(path, stream => JsonSerializer.Deserialize<T>(stream, Options)).GetValueOrDefault();

        public static JsonNode? LoadNode(string path) =>
            ReadJson(path, stream => JsonNode.Parse(stream)).GetValueOrDefault();

        public static void Save<T>(string path, T value)
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
            Retry(() => File.Move(temporary, path, overwrite: true));
        }

        // Another process briefly holding a file or folder open must not fail the operation.
        public static void Retry(Action action)
        {
            for (var attempt = 1;; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception error) when (attempt < 10 && error is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(500);
                }
            }
        }

        // Allowing delete lets the watcher replace a file while a search is reading it.
        private static FileStream OpenShared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // The one edge for reading state: only I/O, access and JSON errors become a failure, so programming errors still surface.
        private static Result<T, string> ReadJson<T>(string path, Func<Stream, T> read) =>
            Attempt.Io(() => Attempt.Json(() =>
            {
                using var stream = OpenShared(path);
                return read(stream);
            })).Bind(parsed => parsed);
    }
}
