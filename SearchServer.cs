using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using CSharpFunctionalExtensions;

namespace SessionMemory
{
    /// <summary>
    /// A resident <c>leann-dotnet --serve</c>, so a search skips the per-process model and index load
    /// (about 0.2 s instead of 13 s). LEANN caches loaded indexes, so the watcher restarts it after each publish.
    /// </summary>
    internal static class SearchServer
    {
        public const int Port = 57392;
        private static readonly HttpClient Client = new()
        {
            BaseAddress = new Uri($"http://localhost:{Port}/"),
            Timeout = TimeSpan.FromSeconds(60)
        };

        public static Process? Start(string root)
        {
            var start = new ProcessStartInfo(Indexer.Leann)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
                     {
                         "--serve", "--port", Port.ToString(CultureInfo.InvariantCulture), "--data-root", root
                     })
                start.ArgumentList.Add(argument);
            // CPU keeps GPU memory free for builds; embedding one query is fast either way.
            start.Environment["LEANN_FORCE_CPU"] = "1";
            return Process.Start(start);
        }

        public static void Stop(Process? owned)
        {
            // Best effort: a server that is not running cannot be asked to stop.
            Get("shutdown", TimeSpan.FromSeconds(3));
            if (owned is { HasExited: false } && !owned.WaitForExit(10_000)) owned.Kill(entireProcessTree: true);
            owned?.Dispose();
            // A server left behind by a killed watcher has no handle here; wait for it to release the port.
            for (var attempt = 0; attempt < 20 && IndexesDirectory() is not null; attempt++) Thread.Sleep(500);
        }

        // Returns null when no server answers for this data root, so the caller falls back to a fresh process.
        public static string? Search(string root, string index, string query, int topK)
        {
            if (!string.Equals(IndexesDirectory(), Path.Combine(root, ".leann", "indexes"), StringComparison.OrdinalIgnoreCase)) return null;
            var url = $"search?index={Uri.EscapeDataString(index)}&query={Uri.EscapeDataString(query)}" +
                      $"&top_k={topK.ToString(CultureInfo.InvariantCulture)}&dedup_threshold=0&show_metadata=true&format=json";
            return Get(url, Client.Timeout)
                .Bind(json => Attempt.Json(() => JsonNode.Parse(json)?["results"] is JsonArray ? json : null))
                .GetValueOrDefault();
        }

        private static string? IndexesDirectory() =>
            Get("health", TimeSpan.FromSeconds(2))
                .Bind(json => Attempt.Json(() => JsonNode.Parse(json)?["indexesDir"].AsString()))
                .GetValueOrDefault();

        // The one network edge: a server that is down, failing or too slow becomes a failure instead of an exception.
        private static Result<string, string> Get(string url, TimeSpan timeout)
        {
            try
            {
                using var cancel = new CancellationTokenSource(timeout);
                return Result.Success<string, string>(Client.GetStringAsync(url, cancel.Token).GetAwaiter().GetResult());
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                return Result.Failure<string, string>(Attempt.Describe(error));
            }
        }
    }
}
