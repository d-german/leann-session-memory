using System.Globalization;
using System.Text.RegularExpressions;

namespace SessionMemory
{
    sealed internal partial class IndexProgress(string root, string tier, string buildId, bool display)
    {
        [GeneratedRegex(@"^\[[^\]\r\n]+\] Batch (\d+)/(\d+) \(\d+%\)", RegexOptions.CultureInvariant)]
        private static partial Regex Batch();
        private readonly object _gate = new();

        public void Observe(string line)
        {
            line = line.Trim();
            if (!line.StartsWith($"[{Indexer.IndexName(tier)}] ", StringComparison.Ordinal)) return;
            var match = Batch().Match(line);
            if (match.Success
                && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var completed)
                && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var total)
                && total > 0 && completed <= total)
                Report("indexing", completed, total);
            else if (line.Contains("Embeddings computed in", StringComparison.Ordinal)) Report("saving");
        }

        public void Report(string phase, int? completed = null, int? total = null, string? error = null)
        {
            var percent = total is > 0 && completed is { } count ? (int?)((long)count * 100 / total.Value) : null;
            lock (_gate)
            {
                // Progress is best effort, so an unavailable status file or console cannot abort an index build.
                Attempt.Io(() => Store.Save(Path.Combine(root, "state", $"{tier}-progress.json"), new
                {
                    At = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    Environment.ProcessId,
                    BuildId = buildId,
                    Phase = phase,
                    CompletedBatches = completed,
                    TotalBatches = total,
                    Percent = percent,
                    Error = error
                }));
                if (!display) return;
                var text = phase switch
                {
                    "loading" => "Loading and splitting transcripts...",
                    "indexing" => $"Indexing: {percent}% ({completed}/{total} batches)",
                    "saving" => "Saving index...",
                    "validating" => "Validating index...",
                    "publishing" => "Publishing index...",
                    "complete" => "Index ready.",
                    "failed" => $"Indexing failed: {error}",
                    _ => phase
                };
                Attempt.Io(() => Console.WriteLine($"{tier}: {text}"));
            }
        }
    }
}
