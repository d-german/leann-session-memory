using System.Diagnostics;
using System.Globalization;

namespace SessionMemory;

/// <summary>Captures transcripts every minute and keeps the recent and archive LEANN indexes current.</summary>
sealed class Watcher
{
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    static readonly TimeSpan Debounce = TimeSpan.FromSeconds(90);
    static readonly TimeSpan MaxDebounce = TimeSpan.FromMinutes(5);
    static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(15);

    // ponytail: LEANN .NET re-embeds a whole index per build, so changed sessions go to a small recent
    // index and the archive is rebuilt only after this many recent bytes accumulate.
    const long ArchiveAfterRecentBytes = 4_000_000;

    readonly string root;
    readonly IReadOnlyList<Family> families;
    readonly Dictionary<string, Task<(TierState? State, Exception? Error)>> running = [];
    readonly Dictionary<string, DateTime> failedAt = [];
    readonly Dictionary<string, string> errors = [];
    DateTime lastChange = DateTime.MinValue;
    DateTime? firstUnbuilt;
    string? corpusHash;
    Process? server;

    public Watcher(string root, IReadOnlyList<Family> families)
    {
        this.root = root;
        this.families = families;
    }

    public static bool RecentDue(bool exists, string signature, string published, DateTime now, DateTime lastChange, DateTime? firstUnbuilt, bool once) =>
        !exists || (signature != published
            && (once || now - lastChange >= Debounce || (firstUnbuilt is { } first && now - first >= MaxDebounce)));

    public async Task<int> RunAsync(bool once, bool force)
    {
        Log(once ? "one-time refresh started" : $"watcher started, process {Environment.ProcessId}");
        while (true)
        {
            try
            {
                Cycle(once, force);
            }
            catch (Exception error)
            {
                errors["capture"] = $"{error.GetType().Name}: {error.Message}";
                Log($"cycle failed: {errors["capture"]}");
                if (once) return 1;
            }
            force = false;
            if (once && running.Count == 0) return errors.Count == 0 ? 0 : 1;
            List<Task> waits = [.. running.Values];
            if (!once) waits.Add(Task.Delay(Interval));
            await (once ? Task.WhenAll(waits) : Task.WhenAny(waits));
        }
    }

    void Cycle(bool once, bool force)
    {
        var published = Collect();
        if (!once && (published || server is not { HasExited: false })) RestartServer();
        var capture = Corpus.Capture(root, families);
        errors.Remove("capture");
        var manifest = capture.Manifest;
        var now = DateTime.UtcNow;
        if (manifest.CorpusHash != corpusHash)
        {
            if (corpusHash is not null)
            {
                lastChange = now;
                firstUnbuilt ??= now;
            }
            corpusHash = manifest.CorpusHash;
            Log($"corpus {manifest.DocumentCount} documents, {manifest.MessageCount} messages; re-read: {string.Join(", ", capture.Reparsed)}");
        }
        var archive = Indexer.LoadState(root, "archive");
        var recent = Indexer.RecentDocuments(manifest.Documents, archive.Documents);
        var recentBytes = recent.Keys.Sum(name => manifest.Documents[name].Bytes);
        var recentDue = force || RecentDue(Indexer.Exists(root, "recent"), Indexer.Signature(recent), Indexer.LoadState(root, "recent").Signature,
            now, lastChange, firstUnbuilt, once);
        // A changed set this large is the archive's job; building both would embed it twice.
        if (archive.Documents.Count > 0 && (force || recentBytes <= ArchiveAfterRecentBytes) && CanStart("recent", force, now) && recentDue)
        {
            Start(Indexer.Stage(root, "recent", recent, manifest.CorpusHash));
            firstUnbuilt = null;
        }
        var archiveDue = force || !Indexer.Exists(root, "archive") || archive.Documents.Count == 0 || recentBytes > ArchiveAfterRecentBytes
            || PurgeDue(archive, now);
        if (CanStart("archive", force, now) && archiveDue)
        {
            var all = manifest.Documents.ToDictionary(pair => pair.Key, pair => pair.Value.Sha256);
            Start(Indexer.Stage(root, "archive", all, manifest.CorpusHash));
        }
        WriteStatus(capture, recent.Count, recentBytes);
    }

    bool CanStart(string tier, bool force, DateTime now) =>
        !running.ContainsKey(tier) && (force || !failedAt.TryGetValue(tier, out var failed) || now - failed >= FailureBackoff);

    // Exclusions made after the archive build began are purged by rebuilding it once they have been quiet for the debounce,
    // so a batch of exclusions costs one rebuild.
    bool PurgeDue(TierState archive, DateTime now)
    {
        var path = Corpus.ExclusionsPath(root);
        if (!File.Exists(path) || !DateTimeOffset.TryParse(archive.PublishedAt, CultureInfo.InvariantCulture, out var published)) return false;
        var changed = File.GetLastWriteTimeUtc(path);
        return changed > (published - TimeSpan.FromSeconds(archive.DurationSeconds)).UtcDateTime && now - changed >= Debounce;
    }

    void Start(StagedBuild staged)
    {
        Log($"{staged.Tier} build started: {staged.Documents.Count} documents");
        running[staged.Tier] = Task.Run(() =>
        {
            try
            {
                return ((TierState?)Indexer.Build(root, staged), (Exception?)null);
            }
            catch (Exception error)
            {
                return ((TierState?)null, (Exception?)error);
            }
        });
    }

    void RestartServer()
    {
        try
        {
            SearchServer.Stop(server);
            server = SearchServer.Start(root);
            errors.Remove("search_server");
            Log($"search server started, process {server?.Id}");
        }
        catch (Exception error)
        {
            server = null;
            errors["search_server"] = $"{error.GetType().Name}: {error.Message}";
            Log($"search server failed: {errors["search_server"]}");
        }
    }

    bool Collect()
    {
        var published = false;
        foreach (var (tier, task) in running.Where(pair => pair.Value.IsCompleted).ToList())
        {
            running.Remove(tier);
            var (state, error) = task.Result;
            if (state is not null)
            {
                Store.Save(Indexer.StatePath(root, tier), state);
                failedAt.Remove(tier);
                errors.Remove(tier);
                published = true;
                Log($"{tier} build published in {state.DurationSeconds:0.#} s: {state.Documents.Count} documents");
            }
            else
            {
                failedAt[tier] = DateTime.UtcNow;
                errors[tier] = $"{error!.GetType().Name}: {error.Message}";
                Log($"{tier} build failed: {errors[tier]}");
            }
        }
        return published;
    }

    void WriteStatus(CaptureReport capture, int recentDocuments, long recentBytes)
    {
        var manifest = capture.Manifest;
        Store.Save(Path.Combine(root, "state", "watcher.json"), new
        {
            At = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            ProcessId = Environment.ProcessId,
            Capture = new
            {
                capture.Changed,
                capture.DurationSeconds,
                capture.Reparsed,
                manifest.CorpusHash,
                manifest.DocumentCount,
                manifest.MessageCount,
                manifest.Bytes,
                FailedFamilies = manifest.Families.Where(pair => !pair.Value.Ok).ToDictionary(pair => pair.Key, pair => pair.Value.Error),
            },
            Recent = new { Documents = recentDocuments, Bytes = recentBytes },
            Building = running.Keys.ToArray(),
            SearchServer = server is { HasExited: false } ? server.Id : (int?)null,
            Errors = errors,
        });
    }

    void Log(string message)
    {
        var path = Path.Combine(root, "logs", "watcher.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".1", overwrite: true);
        File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
    }
}
