using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SessionMemory.Clients;

namespace SessionMemory
{
    internal static partial class SelfTest
    {
        private static void ProgressChecks()
        {
            var temporary = Directory.CreateTempSubdirectory("session-memory-progress-");
            using var cleanup = new TemporaryDirectory(temporary.FullName);
            var root = temporary.FullName;
            var path = Path.Combine(root, "state", "archive-progress.json");
            var progress = new IndexProgress(root, "archive", "test-build", false);
            progress.Report("loading");
            progress.Observe("  [session-memory-conversations-v1] Batch 10/20 (99%) - 7 passages/s");
            var state = Store.LoadNode(path);
            Check(state?["phase"].AsString() == "indexing" && state["percent"]!.GetValue<int>() == 50
                  && state["completed_batches"]!.GetValue<int>() == 10 && state["total_batches"]!.GetValue<int>() == 20,
                "computes progress from completed batches rather than trusting logged percentages");
            foreach (var line in new[]
                     {
                         "[session-memory-conversations-v1] Batch 10/0 (50%)", "[session-memory-conversations-v1] Batch 21/20 (50%)",
                         "[session-memory-conversations-v1] Batch 999999999999/20 (50%)", "[session-memory-conversations-v1-recent] Batch 20/20 (100%)", "not progress"
                     }) progress.Observe(line);
            Check(Store.LoadNode(path)?["percent"]!.GetValue<int>() == 50, "ignores malformed progress and another index's output");
            using var services = Services(Path.Combine(root, "home"), root, []);
            Check(services.GetRequiredService<StatusReport>().Read()["archive-progress"]?["build_id"].AsString() == "test-build",
                "status exposes the current build's live progress");
            progress.Observe("[session-memory-conversations-v1] Embeddings computed in 1.0s");
            Check(Store.LoadNode(path)?["phase"].AsString() == "saving" && Store.LoadNode(path)?["percent"] is null,
                "finishing embeddings advances to saving rather than claiming readiness");
            progress.Report("failed", error: "test failure");
            Check(Store.LoadNode(path)?["phase"].AsString() == "failed" && Store.LoadNode(path)?["error"].AsString() == "test failure",
                "failed builds replace their last percentage with failure details");

            StreamingProgressChecks(root, progress);
            var output = Console.Out;
            using var printed = new StringWriter();
            Console.SetOut(printed);
            try
            {
                var displayed = new IndexProgress(root, "archive", "next-build", true);
                displayed.Observe("[session-memory-conversations-v1] Batch 20/20 (100%)");
                displayed.Report("validating");
                Check(!printed.ToString().Contains("ready", StringComparison.OrdinalIgnoreCase), "100 percent of batches does not announce readiness");
                displayed.Report("complete");
            }
            finally { Console.SetOut(output); }
            Check(printed.ToString().Contains("100% (20/20 batches)") && printed.ToString().Contains("Index ready."), "prints batch percentages and final readiness");

            var failedRoot = Path.Combine(root, "failed-refresh");
            using var failing = Services(Path.Combine(failedRoot, "home"), failedRoot, []);
            using var failedOutput = new StringWriter();
            Console.SetOut(failedOutput);
            try
            {
                Check(failing.GetRequiredService<Watcher>().RunAsync(true, false, true).GetAwaiter().GetResult() == 1,
                    "a foreground refresh returns failure when its model is missing");
            }
            finally { Console.SetOut(output); }
            Check(Store.LoadNode(Path.Combine(failedRoot, "state", "archive-progress.json"))?["phase"].AsString() == "failed"
                  && failedOutput.ToString().Contains("Indexing failed") && !failedOutput.ToString().Contains("Ready."),
                "failed foreground builds publish failed status without announcing readiness");

            var parseRoot = Path.Combine(root, "failed-parser");
            var broken = new LambdaSessionClient("broken", _ => [], _ => throw new InvalidOperationException("parser unavailable"));
            using var parseServices = Services(Path.Combine(parseRoot, "home"), parseRoot, [broken]);
            failedOutput.GetStringBuilder().Clear();
            Console.SetOut(failedOutput);
            try
            {
                Check(parseServices.GetRequiredService<Watcher>().RunAsync(true, false, true).GetAwaiter().GetResult() == 1,
                    "a foreground refresh fails when a history reader records a parsing error");
            }
            finally { Console.SetOut(output); }
            Check(Store.LoadNode(Path.Combine(parseRoot, "state", "watcher.json"))?["errors"]?["capture"].AsString()?.Contains("broken: InvalidOperationException: parser unavailable") == true
                  && !Directory.Exists(Path.Combine(parseRoot, "builds")) && !failedOutput.ToString().Contains("Ready."),
                "a failed history reader records its error and cannot produce a partial ready index");
        }

        private static void StreamingProgressChecks(string root, IndexProgress progress)
        {
            var acknowledgement = Path.Combine(root, "stream-observed");
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.Environment["SESSION_MEMORY_PROGRESS_ACK"] = acknowledgement;
            if (OperatingSystem.IsWindows())
            {
                start.ArgumentList.Add("-NoLogo");
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-Command");
                start.ArgumentList.Add("[Console]::Error.WriteLine(('e' * 131072)); [Console]::Out.WriteLine('[session-memory-conversations-v1] Batch 10/20 (50%)'); " +
                                       "$timer = [Diagnostics.Stopwatch]::StartNew(); while (-not [IO.File]::Exists($env:SESSION_MEMORY_PROGRESS_ACK) -and $timer.Elapsed.TotalSeconds -lt 5) { Start-Sleep -Milliseconds 20 }; " +
                                       "if (-not [IO.File]::Exists($env:SESSION_MEMORY_PROGRESS_ACK)) { exit 7 }; [Console]::Out.WriteLine('finished'); exit 0");
            }
            else
            {
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add("printf '%131072s\\n' e >&2; printf '[session-memory-conversations-v1] Batch 10/20 (50%%)\\n'; " +
                                       "i=0; while [ ! -f \"$SESSION_MEMORY_PROGRESS_ACK\" ] && [ \"$i\" -lt 100 ]; do sleep 0.05; i=$((i+1)); done; " +
                                       "[ -f \"$SESSION_MEMORY_PROGRESS_ACK\" ] || exit 7; printf 'finished\\n'");
            }
            var process = Process.Start(start)!;
            var result = Indexer.Collect(process, line =>
            {
                progress.Observe(line);
                if (line.StartsWith("[session-memory-conversations-v1] Batch", StringComparison.Ordinal)) File.WriteAllText(acknowledgement, "observed");
            });
            Check(result.ExitCode == 0 && result.Output.Contains("finished") && result.Error.Length >= 131072,
                "streams progress before process exit while draining both output pipes and preserving logs");
        }
    }
}
