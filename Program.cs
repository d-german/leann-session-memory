using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpFunctionalExtensions;
using SessionMemory.Clients;
using SessionMemory.Clients.Synthetic;

namespace SessionMemory
{
    /// <summary>A checked change to the exclusion list and the lines that report it; nothing is saved or printed yet.</summary>
    sealed internal record ExclusionUpdate
    {
        public required ImmutableArray<string> Exclusions { get; init; }

        public required ImmutableArray<string> Report { get; init; }
    }

    internal static class Program
    {
        private const string Usage = "Usage: SessionMemory [--root <path>] run [--once] [--force] | search <query> [--top-k <n>] [--json] [--include-current] [--current] | " +
                                     "find <term> [<term> ...] [--top <n>] [--session <id>] [--partial] [--json] [--include-current] [--current] [--since <time>] [--outputs] | " +
                                     "show <session-id>|--current [--last <n>] [--around <regex>] [--context <n>] [--max <n>] [--chars <n>] | " +
                                     "exclude <session-id> | include <session-id> | capture | status | self-test";
        private const string Undetected = "The current session could not be detected; give its session ID instead.";
        private const string ExcludedHint = "Search hides it now; the watcher removes it from both indexes within about 20 minutes.";
        private const string IncludedHint = "The watcher indexes it again within a few minutes.";

        private static int Main(string[] args)
        {
            var arguments = args.ToList();
            if (arguments.Any(argument => argument is "--help" or "-h" or "-?" or "/?")) return Fail(Usage, 0);
            var root = Option(arguments, "--root").Match(Path.GetFullPath, DefaultRoot);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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

        private static string DefaultRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeannSessionMemory");

        // Takes "<name> <value>" out of the arguments and returns the value.
        private static Maybe<string> Option(List<string> arguments, string name)
        {
            var index = arguments.IndexOf(name);
            if (index < 0 || index + 1 >= arguments.Count) return Maybe<string>.None;
            var value = arguments[index + 1];
            arguments.RemoveRange(index, 2);
            return Maybe.From(value);
        }

        // A whole-number option, or the fallback when it is absent.
        public static Result<int, CliError> Number(List<string> arguments, string name, int fallback) =>
            Option(arguments, name).Match(
                value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                    ? Result.Success<int, CliError>(number)
                    : Result.Failure<int, CliError>(new CliError($"'{value}' is not a whole number for {name}.\n{Usage}", 2)),
                () => Result.Success<int, CliError>(fallback));

        // A time without an offset is read as UTC.
        private static Result<DateTimeOffset?, CliError> Since(Maybe<string> text) =>
            text.Match(
                value => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? Result.Success<DateTimeOffset?, CliError>(parsed)
                    : Result.Failure<DateTimeOffset?, CliError>(new CliError($"'{value}' is not a valid time for --since.\n{Usage}", 2)),
                () => Result.Success<DateTimeOffset?, CliError>(null));

        // Agent hosts announce the session they run in; --current uses it.
        private static Result<string, CliError> CurrentSession() => Maybe.From(Corpus.CurrentSession()).ToResult(new CliError(Undetected, 2));

        // The one session to look in: with --current the session this agent runs in, otherwise the one given, if any.
        private static Result<string?, CliError> OnlySession(bool current, string? session) =>
            current ? CurrentSession().Map(string? (id) => id) : Result.Success<string?, CliError>(session);

        private static int Run(string root, string home, bool once, bool force)
        {
            using var single = new Mutex(true, @"Local\LeannSessionMemoryWatcher", out var created);
            if (!created) return Fail("Another SessionMemory watcher or refresh is already running.", once ? 3 : 0);
            SyntheticTranscripts.EnsureCanary(root);
            return new Watcher(new ClientContext(home, root), ClientRegistry.All).RunAsync(once, force).GetAwaiter().GetResult();
        }

        private static int Search(string root, List<string> arguments)
        {
            var json = arguments.Remove("--json");
            var includeCurrent = arguments.Remove("--include-current");
            var current = arguments.Remove("--current");
            // --current lets an agent whose context was compacted search its own session.
            return Number(arguments, "--top-k", 8)
                .Bind(topK => OnlySession(current, null).Map(only => new SearchRequest
                {
                    Root = root,
                    Query = string.Join(' ', arguments.Skip(1)),
                    TopK = Math.Clamp(topK, 1, 50),
                    Json = json,
                    IncludeCurrent = includeCurrent || current,
                    OnlySession = only
                }))
                .Match(Indexer.Search, Fail);
        }

        private static int Capture(string root, string home)
        {
            var report = Corpus.Capture(new ClientContext(home, root), ClientRegistry.All);
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
            return Number(arguments, "--top", 8)
                .Map(top => new FindRequest
                {
                    Root = root,
                    Home = home,
                    Terms = [],
                    Top = Math.Clamp(top, 1, 50),
                    Json = json,
                    Partial = partial,
                    IncludeCurrent = includeCurrent || current,
                    Outputs = outputs
                })
                .Bind(request => Scope(request, arguments, current))
                .Match(Finder.Run, Fail);
        }

        // Adds what the remaining arguments give: the terms, the --since time and the one session to look in.
        private static Result<FindRequest, CliError> Scope(FindRequest request, List<string> arguments, bool current)
        {
            var session = Option(arguments, "--session");
            var since = Option(arguments, "--since");
            var terms = arguments.Skip(1).Where(term => term.Trim().Length > 0).ToImmutableArray();
            var unknown = terms.FirstOrDefault(term => term.StartsWith("--", StringComparison.Ordinal));
            return Result.Success<FindRequest, CliError>(request with
                {
                    Terms = terms
                })
                .Ensure(_ => unknown is null, _ => new CliError($"Unknown option '{unknown}'.\n{Usage}", 2))
                .Bind(scoped => Since(since).Map(time => scoped with
                {
                    Since = time
                }))
                .Ensure(_ => !current || session.HasNoValue, new CliError("Use either --current or --session, not both.", 2))
                .Bind(scoped => OnlySession(current, session.Map(value => value.Trim()).GetValueOrDefault()).Map(only => scoped with
                {
                    Only = only
                }));
        }

        private static int Show(string root, List<string> arguments)
        {
            var current = arguments.Remove("--current");
            return ShowOptions(root, arguments, current)
                .Bind(Compact.Show)
                .Tap(Compact.Write)
                .Match(_ => 0, Fail);
        }

        // Reads --last, --context, --max and --chars in this order, so the first bad number is the one reported, then --around and the session.
        private static Result<ShowRequest, CliError> ShowOptions(string root, List<string> arguments, bool current) =>
            Number(arguments, "--last", 10)
                .Map(last => new ShowRequest
                {
                    Root = root,
                    Id = "",
                    Last = Math.Clamp(last, 1, 200)
                })
                .Bind(request => Number(arguments, "--context", 0).Map(context => request with
                {
                    Context = Math.Clamp(context, 0, 5)
                }))
                .Bind(request => Number(arguments, "--max", 6).Map(max => request with
                {
                    Max = Math.Clamp(max, 1, 50)
                }))
                .Bind(request => Number(arguments, "--chars", 1200).Map(chars => request with
                {
                    Chars = Math.Clamp(chars, 200, 20_000)
                }))
                .Map(request => request with
                {
                    Around = Option(arguments, "--around").GetValueOrDefault()
                })
                .Bind(request => ShowTarget(arguments, current).Map(id => request with
                {
                    Id = id
                }));

        // The session to show: the ID given, or with --current the session this agent runs in.
        private static Result<string, CliError> ShowTarget(List<string> arguments, bool current) =>
            Result.Success<List<string>, CliError>(arguments)
                .Ensure(rest => !current || rest.Count != 2 || rest[1].StartsWith("--", StringComparison.Ordinal),
                    new CliError("Use either a session ID or --current, not both.", 2))
                .Ensure(rest => rest.Count == (current ? 1 : 2) && !rest.Skip(1).Any(argument => argument.StartsWith("--", StringComparison.Ordinal)),
                    new CliError(Usage, 2))
                .Bind(rest => current ? CurrentSession() : Result.Success<string, CliError>(rest[1].Trim()));

        // Takes a full session ID or a prefix of at least 8 characters, as shown in search results.
        private static int Exclude(string root, string id, bool exclude) =>
            ExclusionChange(root, id, exclude)
                .Tap(update => Print(update.Report))
                .Tap(update => Store.Save(Corpus.ExclusionsPath(root), update.Exclusions))
                .Tap(() => Console.WriteLine(exclude ? ExcludedHint : IncludedHint))
                .Match(_ => 0, Fail);

        // Checks the ID and works out the new exclusion list; nothing is saved or printed.
        public static Result<ExclusionUpdate, CliError> ExclusionChange(string root, string id, bool exclude) =>
            Result.Success<string, CliError>(id.Trim())
                .Ensure(trimmed => trimmed.Length >= 8, new CliError("Give at least 8 characters of the session ID.", 2))
                .Ensure(trimmed => !Indexer.CanaryId.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase),
                    new CliError("The canary session validates every index build and cannot be excluded.", 2))
                .Bind(trimmed => exclude ? AddExclusion(root, trimmed) : RemoveExclusion(root, trimmed));

        private static Result<ExclusionUpdate, CliError> AddExclusion(string root, string id)
        {
            var documents = Store.Load<Manifest>(Path.Combine(root, "state", "manifest.json"))?.Documents.Values ?? Enumerable.Empty<DocumentMeta>();
            ImmutableArray<string> prefix = [id];
            var matches = documents.Where(document => Corpus.IsExcluded(document.SessionId, prefix)).ToImmutableArray();
            var exclusions = Corpus.LoadExclusions(root);
            return Result.Success<ImmutableArray<DocumentMeta>, CliError>(matches)
                .Ensure(found => !found.IsEmpty, new CliError($"No indexed session starts with '{id}'.", 1))
                .Map(found => new ExclusionUpdate
                {
                    Exclusions = exclusions.Contains(id, StringComparer.OrdinalIgnoreCase) ? [.. exclusions] : [.. exclusions, id],
                    Report = [.. found.Select(match => $"Excluded {match.SessionId} ({match.Client}) {match.Title}")]
                });
        }

        private static Result<ExclusionUpdate, CliError> RemoveExclusion(string root, string id)
        {
            var exclusions = Corpus.LoadExclusions(root);
            var kept = exclusions.Where(entry => !entry.Equals(id, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
            return Result.Success<ImmutableArray<string>, CliError>(kept)
                .Ensure(rest => rest.Length < exclusions.Count, new CliError($"'{id}' is not excluded.", 1))
                .Map(rest => new ExclusionUpdate
                {
                    Exclusions = rest,
                    Report = [$"Included {id} again."]
                });
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

        private static void Print(ImmutableArray<string> lines)
        {
            foreach (var line in lines) Console.WriteLine(line);
        }

        private static int Fail(CliError error) => Fail(error.Message, error.ExitCode);

        private static int Fail(string message, int code)
        {
            Console.Error.WriteLine(message);
            return code;
        }
    }
}
