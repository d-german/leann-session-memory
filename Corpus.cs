using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SessionMemory;

sealed class DocumentMeta
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

sealed class FamilyReport
{
    public bool Ok { get; set; } = true;

    public string Error { get; set; } = "";

    public string? Fingerprint { get; set; }

    public Dictionary<string, long> Stats { get; set; } = [];
}

sealed class Manifest
{
    public int Version { get; set; }

    public string GeneratedAt { get; set; } = "";

    public string CorpusHash { get; set; } = "";

    public Dictionary<string, DocumentMeta> Documents { get; set; } = [];

    public Dictionary<string, FamilyReport> Families { get; set; } = [];

    public int DocumentCount { get; set; }

    public long MessageCount { get; set; }

    public long Bytes { get; set; }
}

sealed class CaptureReport
{
    public CaptureReport(bool changed, Manifest manifest, double durationSeconds, IReadOnlyList<string> reparsed)
    {
        Changed = changed;
        Manifest = manifest;
        DurationSeconds = durationSeconds;
        Reparsed = reparsed;
    }

    public bool Changed { get; }

    public Manifest Manifest { get; }

    public double DurationSeconds { get; }

    public IReadOnlyList<string> Reparsed { get; }
}

/// <summary>Turns transcripts into the Markdown corpus LEANN indexes, rewriting only documents that changed.</summary>
static class Corpus
{
    const int ManifestVersion = 3;
    static readonly UTF8Encoding NoBom = new(false);

    public static CaptureReport Capture(string root, IReadOnlyList<Family> families)
    {
        var stopwatch = Stopwatch.StartNew();
        var corpus = Path.Combine(root, "corpus");
        var manifestPath = Path.Combine(root, "state", "manifest.json");
        var previous = Store.Load<Manifest>(manifestPath) ?? new Manifest();
        var documents = new Dictionary<string, DocumentMeta>();
        var contents = new Dictionary<string, string>();
        var reports = new Dictionary<string, FamilyReport>();
        var reparsed = new List<string>();
        foreach (var family in families)
        {
            var fingerprint = Fingerprint(family.Sources());
            if (previous.Version == ManifestVersion && previous.Families.TryGetValue(family.Name, out var known) && known.Ok && known.Fingerprint == fingerprint)
            {
                reports[family.Name] = known;
                continue;
            }
            reparsed.Add(family.Name);
            try
            {
                var result = family.Parse();
                reports[family.Name] = new FamilyReport { Fingerprint = fingerprint, Stats = result.Stats };
                foreach (var document in result.Documents)
                {
                    var (content, kept, sourceRef) = Transcript.Render(document);
                    if (kept == 0) continue;
                    var bytes = Encoding.UTF8.GetBytes(content);
                    var name = Transcript.DocumentName(document);
                    contents[name] = content;
                    documents[name] = new DocumentMeta
                    {
                        Family = document.Family, Client = document.Client, SessionId = document.SessionId, SourceRef = sourceRef,
                        MessageCount = kept, Sha256 = Transcript.Sha256(bytes), Bytes = bytes.Length,
                        LastActivity = document.Messages.Select(message => message.Timestamp).Where(time => time.Length > 0)
                            .DefaultIfEmpty("").Max(StringComparer.Ordinal)!,
                        Title = Transcript.CleanTitle(document.Title),
                    };
                }
            }
            catch (Exception error)
            {
                reports[family.Name] = new FamilyReport { Ok = false, Error = $"{error.GetType().Name}: {error.Message}" };
            }
        }
        // Unchanged or failed families keep their previous documents; a fresh parse is authoritative.
        var parsed = reparsed.Where(name => reports[name].Ok).ToHashSet();
        foreach (var (name, meta) in previous.Documents)
        {
            if (!parsed.Contains(meta.Family) && !documents.ContainsKey(name) && File.Exists(Path.Combine(corpus, name)))
                documents[name] = meta;
        }
        var exclusions = LoadExclusions(root);
        foreach (var name in documents.Where(pair => IsExcluded(pair.Value.SessionId, exclusions)).Select(pair => pair.Key).ToList())
        {
            documents.Remove(name);
            contents.Remove(name);
        }
        Directory.CreateDirectory(corpus);
        foreach (var (name, content) in contents)
        {
            var target = Path.Combine(corpus, name);
            if (previous.Documents.TryGetValue(name, out var old) && old.Sha256 == documents[name].Sha256 && File.Exists(target)) continue;
            var temporary = Path.Combine(corpus, $".{name}.{Guid.NewGuid():N}.tmp");
            // Same bytes as Python's text-mode write on Windows.
            File.WriteAllText(temporary, content.Replace("\n", "\r\n"), NoBom);
            Store.Retry(() => File.Move(temporary, target, overwrite: true));
        }
        foreach (var stale in Directory.EnumerateFiles(corpus, "*.md").Where(path => !documents.ContainsKey(Path.GetFileName(path))).ToList())
            File.Delete(stale);
        var manifest = new Manifest
        {
            Version = ManifestVersion,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture),
            CorpusHash = CorpusHash(documents),
            Documents = documents,
            Families = reports,
            DocumentCount = documents.Count,
            MessageCount = documents.Values.Sum(document => (long)document.MessageCount),
            Bytes = documents.Values.Sum(document => document.Bytes),
        };
        var changed = manifest.CorpusHash != previous.CorpusHash;
        if (changed || reparsed.Count > 0 || previous.Version != ManifestVersion) Store.Save(manifestPath, manifest);
        return new CaptureReport(changed, manifest, Math.Round(stopwatch.Elapsed.TotalSeconds, 3), reparsed);
    }

    public static string ExclusionsPath(string root) => Path.Combine(root, "state", "excluded.json");

    public static List<string> LoadExclusions(string root) => Store.Load<List<string>>(ExclusionsPath(root)) ?? [];

    // A prefix also covers Claude subagent sessions, whose IDs start with the parent's.
    public static bool IsExcluded(string sessionId, IReadOnlyCollection<string> exclusions) =>
        exclusions.Any(prefix => sessionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    static string Fingerprint(IEnumerable<string> files)
    {
        // The build id makes a new parser re-read every family once.
        var builder = new StringBuilder().Append(typeof(Corpus).Assembly.ManifestModule.ModuleVersionId).Append('\n');
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            if (info.Exists) builder.Append(file).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }
        return Transcript.Sha256(builder.ToString());
    }

    static string CorpusHash(Dictionary<string, DocumentMeta> documents)
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
static class Store
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    public static T? Load<T>(string path) where T : class
    {
        try
        {
            using var stream = OpenShared(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static JsonNode? LoadNode(string path)
    {
        try
        {
            using var stream = OpenShared(path);
            return JsonNode.Parse(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

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
        for (var attempt = 1; ; attempt++)
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
    static FileStream OpenShared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
