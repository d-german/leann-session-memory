using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SessionMemory
{
    internal static class Program
    {
        private const string Usage = "Usage: SessionMemory [--root <path>] run [--once] [--force] | search <query> [--top-k <n>] [--json] [--include-current] [--current] | " +
                                     "find <term> [<term> ...] [--top <n>] [--session <id>] [--partial] [--json] [--include-current] [--current] [--since <time>] [--outputs] | " +
                                     "show <session-id>|--current [--last <n>] [--around <regex>] [--context <n>] [--max <n>] [--chars <n>] | " +
                                     "exclude <session-id> | include <session-id> | capture | status | self-test";
        private const string Undetected = "The current session could not be detected; give its session ID instead.";

        private static int Main(string[] args)
        {
            var arguments = args.ToList();
            if (arguments.Any(argument => argument is "--help" or "-h" or "-?" or "/?")) return Fail(Usage, 0);
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeannSessionMemory");
            if (Option(arguments, "--root") is { } custom) root = Path.GetFullPath(custom);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            try
            {
                return arguments.FirstOrDefault() switch
                {
                    "run" => Run(root, home, arguments.Contains("--once"), arguments.Contains("--force")),
                    "search" when arguments.Count > 1 => Search(root, arguments),
                    "find" when arguments.Count > 1 => Find(root, home, arguments),
                    "show" when arguments.Count > 1 => Show(root, arguments),
                    "exclude" when arguments.Count > 1 => Exclude(root, arguments[1], true),
                    "include" when arguments.Count > 1 => Exclude(root, arguments[1], false),
                    "capture" => Capture(root, home),
                    "status" => Status(root),
                    "self-test" => SelfTest.Run(),
                    _ => Fail(Usage, 2)
                };
            }
            catch (Exception error) when (error is FormatException or OverflowException)
            {
                // A numeric option such as --top-k was given something other than a whole number.
                return Fail($"{error.Message}\n{Usage}", 2);
            }
        }

        private static string? Option(List<string> arguments, string name)
        {
            var index = arguments.IndexOf(name);
            if (index < 0 || index + 1 >= arguments.Count) return null;
            var value = arguments[index + 1];
            arguments.RemoveRange(index, 2);
            return value;
        }

        private static int Run(string root, string home, bool once, bool force)
        {
            using var single = new Mutex(true, @"Local\LeannSessionMemoryWatcher", out var created);
            if (!created) return Fail("Another SessionMemory watcher or refresh is already running.", once ? 3 : 0);
            Parsers.EnsureCanary(root);
            return new Watcher(root, Parsers.All(home, root)).RunAsync(once, force).GetAwaiter().GetResult();
        }

        private static int Search(string root, List<string> arguments)
        {
            var json = arguments.Remove("--json");
            var includeCurrent = arguments.Remove("--include-current");
            var current = arguments.Remove("--current");
            var topK = Option(arguments, "--top-k") is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : 8;
            // --current lets an agent whose context was compacted search its own session.
            var only = current ? Corpus.CurrentSession() : null;
            if (current && only is null) return Fail(Undetected, 2);
            return Indexer.Search(root, string.Join(' ', arguments.Skip(1)), Math.Clamp(topK, 1, 50), json, includeCurrent || current, only);
        }

        private static int Capture(string root, string home)
        {
            var report = Corpus.Capture(root, Parsers.All(home, root));
            var manifest = report.Manifest;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                report.Changed,
                report.DurationSeconds,
                report.Reparsed,
                manifest.CorpusHash,
                manifest.DocumentCount,
                manifest.MessageCount,
                manifest.Bytes,
                manifest.Families
            }, Store.Options));
            return 0;
        }

        private static int Find(string root, string home, List<string> arguments)
        {
            var json = arguments.Remove("--json");
            var partial = arguments.Remove("--partial");
            var includeCurrent = arguments.Remove("--include-current");
            var current = arguments.Remove("--current");
            var outputs = arguments.Remove("--outputs");
            var top = Option(arguments, "--top") is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : 8;
            var session = Option(arguments, "--session");
            var sinceText = Option(arguments, "--since");
            var terms = arguments.Skip(1).Where(term => term.Trim().Length > 0).ToList();
            if (terms.FirstOrDefault(term => term.StartsWith("--", StringComparison.Ordinal)) is { } unknown) return Fail($"Unknown option '{unknown}'.\n{Usage}", 2);
            DateTimeOffset? since = null;
            if (sinceText is not null)
            {
                // A time without an offset is read as UTC.
                if (!DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    return Fail($"'{sinceText}' is not a valid time for --since.\n{Usage}", 2);
                since = parsed;
            }
            if (current && session is not null) return Fail("Use either --current or --session, not both.", 2);
            var only = current ? Corpus.CurrentSession() : session?.Trim();
            if (current && only is null) return Fail(Undetected, 2);
            return Finder.Run(root, home, terms, Math.Clamp(top, 1, 50), json, only, partial, includeCurrent || current, since: since, outputs: outputs);
        }

        private static int Show(string root, List<string> arguments)
        {
            int Number(string name, int fallback) => Option(arguments, name) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
            var current = arguments.Remove("--current");
            var (last, context, max, chars) = (Number("--last", 10), Number("--context", 0), Number("--max", 6), Number("--chars", 1200));
            var around = Option(arguments, "--around");
            if (current && arguments.Count == 2 && !arguments[1].StartsWith("--", StringComparison.Ordinal)) return Fail("Use either a session ID or --current, not both.", 2);
            if (arguments.Count != (current ? 1 : 2) || arguments.Skip(1).Any(argument => argument.StartsWith("--", StringComparison.Ordinal))) return Fail(Usage, 2);
            var id = current ? Corpus.CurrentSession() : arguments[1].Trim();
            if (id is null) return Fail(Undetected, 2);
            try
            {
                Compact.Write(Compact.Show(root, id, Math.Clamp(last, 1, 200), around, Math.Clamp(context, 0, 5), Math.Clamp(max, 1, 50),
                    Math.Clamp(chars, 200, 20_000)));
                return 0;
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                return Fail(error.Message, 2);
            }
        }

        // Takes a full session ID or a prefix of at least 8 characters, as shown in search results.
        private static int Exclude(string root, string id, bool exclude)
        {
            id = id.Trim();
            if (id.Length < 8) return Fail("Give at least 8 characters of the session ID.", 2);
            if (Indexer.CanaryId.StartsWith(id, StringComparison.OrdinalIgnoreCase))
                return Fail("The canary session validates every index build and cannot be excluded.", 2);
            var exclusions = Corpus.LoadExclusions(root);
            var code = exclude ? AddExclusion(root, exclusions, id) : RemoveExclusion(exclusions, id);
            if (code != 0) return code;
            Store.Save(Corpus.ExclusionsPath(root), exclusions);
            Console.WriteLine(exclude
                ? "Search hides it now; the watcher removes it from both indexes within about 20 minutes."
                : "The watcher indexes it again within a few minutes.");
            return 0;
        }

        private static int AddExclusion(string root, List<string> exclusions, string id)
        {
            var documents = Store.Load<Manifest>(Path.Combine(root, "state", "manifest.json"))?.Documents.Values ?? Enumerable.Empty<DocumentMeta>();
            var matches = documents.Where(document => Corpus.IsExcluded(document.SessionId, [id])).ToList();
            if (matches.Count == 0) return Fail($"No indexed session starts with '{id}'.", 1);
            if (!exclusions.Contains(id, StringComparer.OrdinalIgnoreCase)) exclusions.Add(id);
            foreach (var match in matches) Console.WriteLine($"Excluded {match.SessionId} ({match.Client}) {match.Title}");
            return 0;
        }

        private static int RemoveExclusion(List<string> exclusions, string id)
        {
            if (exclusions.RemoveAll(entry => entry.Equals(id, StringComparison.OrdinalIgnoreCase)) == 0) return Fail($"'{id}' is not excluded.", 1);
            Console.WriteLine($"Included {id} again.");
            return 0;
        }

        private static int Status(string root)
        {
            var status = new JsonObject();
            foreach (var name in new[]
                     {
                         "watcher", "manifest", "archive-state", "recent-state"
                     })
            {
                var node = Store.LoadNode(Path.Combine(root, "state", $"{name}.json"));
                (node as JsonObject)?.Remove("documents");
                status[name] = node;
            }
            status["excluded"] = JsonSerializer.SerializeToNode(Corpus.LoadExclusions(root));
            Console.WriteLine(status.ToJsonString(Store.Options));
            return 0;
        }

        private static int Fail(string message, int code)
        {
            Console.Error.WriteLine(message);
            return code;
        }
    }
}
