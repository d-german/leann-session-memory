using System.Diagnostics;
using System.Globalization;

namespace SessionMemory
{
    /// <summary>Captures transcripts every minute and keeps the recent and archive LEANN indexes current.</summary>
    sealed internal class Watcher
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan MaxDebounce = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(15);

        // ponytail: LEANN .NET re-embeds a whole index per build, so changed sessions go to a small recent
        // index and the archive is rebuilt only after this many recent bytes accumulate.
        private const long ArchiveAfterRecentBytes = 4_000_000;

        private readonly string _root;
        private readonly IReadOnlyList<Family> _families;
        private readonly Dictionary<string, Task<(TierState? State, Exception? Error)>> _running = [];
        private readonly Dictionary<string, DateTime> _failedAt = [];
        private readonly Dictionary<string, string> _errors = [];
        private DateTime _lastChange = DateTime.MinValue;
        private DateTime? _firstUnbuilt;
        private string? _corpusHash;
        private Process? _server;

        public Watcher(string root, IReadOnlyList<Family> families)
        {
            _root = root;
            _families = families;
        }

        public static bool RecentDue(bool exists, string signature, string published, DateTime now, DateTime lastChange, DateTime? firstUnbuilt, bool once) =>
            !exists || signature != published
            && (once || now - lastChange >= Debounce || firstUnbuilt is { } first && now - first >= MaxDebounce);

        public async Task<int> RunAsync(bool once, bool force)
        {
            Log(once ? "one-time refresh started" : $"watcher started, process {Environment.ProcessId}");
            while (true)
            {
                var cycled = TryCycle(once, force);
                if (once && !cycled) return 1;
                force = false;
                if (once && _running.Count == 0) return _errors.Count == 0 ? 0 : 1;
                await WaitForWorkAsync(once);
            }
        }

        private bool TryCycle(bool once, bool force)
        {
            try
            {
                Cycle(once, force);
                return true;
            }
            catch (Exception error)
            {
                _errors["capture"] = $"{error.GetType().Name}: {error.Message}";
                Log($"cycle failed: {_errors["capture"]}");
                return false;
            }
        }

        private Task WaitForWorkAsync(bool once)
        {
            List<Task> waits = [.. _running.Values];
            if (!once) waits.Add(Task.Delay(Interval));
            return once ? Task.WhenAll(waits) : Task.WhenAny(waits);
        }

        private void Cycle(bool once, bool force)
        {
            var published = Collect();
            if (!once && (published || _server is not { HasExited: false })) RestartServer();
            var capture = Corpus.Capture(_root, _families);
            _errors.Remove("capture");
            var manifest = capture.Manifest;
            var now = DateTime.UtcNow;
            TrackCorpusChange(capture, now);
            var archive = Indexer.LoadState(_root, "archive");
            var recent = Indexer.RecentDocuments(manifest.Documents, archive.Documents);
            var recentBytes = recent.Keys.Sum(name => manifest.Documents[name].Bytes);
            if (RecentBuildDue(archive, recent, recentBytes, once, force, now))
            {
                Start(Indexer.Stage(_root, "recent", recent, manifest.CorpusHash));
                _firstUnbuilt = null;
            }
            if (ArchiveBuildDue(archive, recentBytes, force, now))
            {
                var all = manifest.Documents.ToDictionary(pair => pair.Key, pair => pair.Value.Sha256);
                Start(Indexer.Stage(_root, "archive", all, manifest.CorpusHash));
            }
            WriteStatus(capture, recent.Count, recentBytes);
        }

        private void TrackCorpusChange(CaptureReport capture, DateTime now)
        {
            var manifest = capture.Manifest;
            if (manifest.CorpusHash == _corpusHash) return;
            if (_corpusHash is not null)
            {
                _lastChange = now;
                _firstUnbuilt ??= now;
            }
            _corpusHash = manifest.CorpusHash;
            Log($"corpus {manifest.DocumentCount} documents, {manifest.MessageCount} messages; re-read: {string.Join(", ", capture.Reparsed)}");
        }

        private bool RecentBuildDue(TierState archive, Dictionary<string, string> recent, long recentBytes, bool once, bool force, DateTime now)
        {
            var recentDue = force || RecentDue(Indexer.Exists(_root, "recent"), Indexer.Signature(recent), Indexer.LoadState(_root, "recent").Signature,
                now, _lastChange, _firstUnbuilt, once);
            // A changed set this large is the archive's job; building both would embed it twice.
            return archive.Documents.Count > 0 && (force || recentBytes <= ArchiveAfterRecentBytes) && CanStart("recent", force, now) && recentDue;
        }

        private bool ArchiveBuildDue(TierState archive, long recentBytes, bool force, DateTime now)
        {
            var archiveDue = force || !Indexer.Exists(_root, "archive") || archive.Documents.Count == 0 || recentBytes > ArchiveAfterRecentBytes
                             || PurgeDue(archive, now);
            return CanStart("archive", force, now) && archiveDue;
        }

        private bool CanStart(string tier, bool force, DateTime now) =>
            !_running.ContainsKey(tier) && (force || !_failedAt.TryGetValue(tier, out var failed) || now - failed >= FailureBackoff);

        // Exclusions made after the archive build began are purged by rebuilding it once they have been quiet for the debounce,
        // so a batch of exclusions costs one rebuild.
        private bool PurgeDue(TierState archive, DateTime now)
        {
            var path = Corpus.ExclusionsPath(_root);
            if (!File.Exists(path) || !DateTimeOffset.TryParse(archive.PublishedAt, CultureInfo.InvariantCulture, out var published)) return false;
            var changed = File.GetLastWriteTimeUtc(path);
            return changed > (published - TimeSpan.FromSeconds(archive.DurationSeconds)).UtcDateTime && now - changed >= Debounce;
        }

        private void Start(StagedBuild staged)
        {
            Log($"{staged.Tier} build started: {staged.Documents.Count} documents");
            _running[staged.Tier] = Task.Run(() =>
            {
                try
                {
                    return ((TierState?)Indexer.Build(_root, staged), null);
                }
                catch (Exception error)
                {
                    return ((TierState?)null, (Exception?)error);
                }
            });
        }

        private void RestartServer()
        {
            try
            {
                SearchServer.Stop(_server);
                _server = SearchServer.Start(_root);
                _errors.Remove("search_server");
                Log($"search server started, process {_server?.Id}");
            }
            catch (Exception error)
            {
                _server = null;
                _errors["search_server"] = $"{error.GetType().Name}: {error.Message}";
                Log($"search server failed: {_errors["search_server"]}");
            }
        }

        private bool Collect()
        {
            var published = false;
            foreach (var (tier, task) in _running.Where(pair => pair.Value.IsCompleted).ToList())
            {
                _running.Remove(tier);
                var (state, error) = task.Result;
                if (state is not null)
                {
                    Store.Save(Indexer.StatePath(_root, tier), state);
                    _failedAt.Remove(tier);
                    _errors.Remove(tier);
                    published = true;
                    Log($"{tier} build published in {state.DurationSeconds:0.#} s: {state.Documents.Count} documents");
                }
                else
                {
                    _failedAt[tier] = DateTime.UtcNow;
                    _errors[tier] = $"{error!.GetType().Name}: {error.Message}";
                    Log($"{tier} build failed: {_errors[tier]}");
                }
            }
            return published;
        }

        private void WriteStatus(CaptureReport capture, int recentDocuments, long recentBytes)
        {
            var manifest = capture.Manifest;
            Store.Save(Path.Combine(_root, "state", "watcher.json"), new
            {
                At = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                Environment.ProcessId,
                Capture = new
                {
                    capture.Changed,
                    capture.DurationSeconds,
                    capture.Reparsed,
                    manifest.CorpusHash,
                    manifest.DocumentCount,
                    manifest.MessageCount,
                    manifest.Bytes,
                    FailedFamilies = manifest.Families.Where(pair => !pair.Value.Ok).ToDictionary(pair => pair.Key, pair => pair.Value.Error)
                },
                Recent = new
                {
                    Documents = recentDocuments,
                    Bytes = recentBytes
                },
                Building = _running.Keys.ToArray(),
                SearchServer = _server is { HasExited: false } ? _server.Id : (int?)null,
                Errors = _errors
            });
        }

        private void Log(string message)
        {
            var path = Path.Combine(_root, "logs", "watcher.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".1", overwrite: true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
        }
    }
}
