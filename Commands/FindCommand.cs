using System.Collections.Immutable;
using System.Globalization;
using CSharpFunctionalExtensions;

namespace SessionMemory.Commands
{
    /// <summary>find: exact-term search over the raw stores of every client.</summary>
    sealed internal class FindCommand : ICommand
    {
        private readonly Finder _finder;
        private readonly CurrentSession _currentSession;

        public FindCommand(Finder finder, CurrentSession currentSession)
        {
            _finder = finder;
            _currentSession = currentSession;
        }

        public string Verb => "find";

        public bool RequiresArguments => true;

        public int Execute(CommandArguments arguments)
        {
            var json = arguments.Flag("--json");
            var partial = arguments.Flag("--partial");
            var includeCurrent = arguments.Flag("--include-current");
            var current = arguments.Flag("--current");
            var outputs = arguments.Flag("--outputs");
            return arguments.Number("--top", 8)
                .Map(top => new FindRequest
                {
                    Terms = [],
                    Top = Math.Clamp(top, 1, 50),
                    Json = json,
                    Partial = partial,
                    IncludeCurrent = includeCurrent || current,
                    Outputs = outputs
                })
                .Bind(request => Scope(request, arguments, current))
                .Map(_finder.Find)
                .Match(output => CommandLine.Write(output, json), CommandLine.Fail);
        }

        // Adds what the remaining arguments give: the terms, the --since time and the one session to look in.
        private Result<FindRequest, CliError> Scope(FindRequest request, CommandArguments arguments, bool current)
        {
            var session = arguments.Option("--session");
            var since = arguments.Option("--since");
            var terms = arguments.Operands.Where(term => term.Trim().Length > 0).ToImmutableArray();
            var unknown = terms.FirstOrDefault(term => term.StartsWith("--", StringComparison.Ordinal));
            return Result.Success<FindRequest, CliError>(request with
                {
                    Terms = terms
                })
                .Ensure(_ => unknown is null, _ => new CliError($"Unknown option '{unknown}'.\n{CommandLine.Usage}", 2))
                .Bind(scoped => Since(since).Map(time => scoped with
                {
                    Since = time
                }))
                .Ensure(_ => !current || session.HasNoValue, new CliError("Use either --current or --session, not both.", 2))
                .Bind(scoped => _currentSession.Scope(current, session.Map(value => value.Trim()).GetValueOrDefault()).Map(only => scoped with
                {
                    Only = only
                }));
        }

        // A time without an offset is read as UTC.
        private static Result<DateTimeOffset?, CliError> Since(Maybe<string> text) =>
            text.Match(
                value => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? Result.Success<DateTimeOffset?, CliError>(parsed)
                    : Result.Failure<DateTimeOffset?, CliError>(new CliError($"'{value}' is not a valid time for --since.\n{CommandLine.Usage}", 2)),
                () => Result.Success<DateTimeOffset?, CliError>(null));
    }
}
