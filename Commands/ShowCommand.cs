using System.Collections.Immutable;
using System.Globalization;
using CSharpFunctionalExtensions;

namespace SessionMemory.Commands
{
    /// <summary>show: one session's last messages, or the messages matching a pattern.</summary>
    sealed internal class ShowCommand : ICommand
    {
        private readonly SessionReader _reader;
        private readonly CurrentSession _currentSession;

        public ShowCommand(SessionReader reader, CurrentSession currentSession)
        {
            _reader = reader;
            _currentSession = currentSession;
        }

        public string Verb => "show";

        public bool RequiresArguments => true;

        public int Execute(CommandArguments arguments)
        {
            var current = arguments.Flag("--current");
            return Options(arguments, current)
                .Bind(_reader.Show)
                .Tap(Compact.Write)
                .Match(_ => 0, CommandLine.Fail);
        }

        // Reads numeric options before resolving the session.
        private Result<ShowRequest, CliError> Options(CommandArguments arguments, bool current) =>
            arguments.Number("--last", 10)
                .Map(last => new ShowRequest
                {
                    Id = "",
                    Last = Math.Clamp(last, 1, 200)
                })
                .Bind(request => arguments.Number("--context", 0).Map(context => request with
                {
                    Context = Math.Clamp(context, 0, 5)
                }))
                .Bind(request => arguments.Number("--max", 6).Map(max => request with
                {
                    Max = Math.Clamp(max, 1, 50)
                }))
                .Bind(request => arguments.Number("--chars", 1200).Map(chars => request with
                {
                    Chars = Math.Clamp(chars, 200, 20_000)
                }))
                .Bind(request => MessageNumber(arguments).Map(number => request with { MessageNumber = number }))
                .Bind(request => Offset(arguments).Bind(offset =>
                    offset is not null && request.MessageNumber is null
                        ? Result.Failure<ShowRequest, CliError>(new CliError("--offset requires --message.", 2))
                        : Result.Success<ShowRequest, CliError>(request with { Offset = offset ?? 0 })))
                .Map(request => request with
                {
                    Around = arguments.Option("--around").GetValueOrDefault()
                })
                .Ensure(request => request.MessageNumber is null || request.Around is null,
                    new CliError("Use either --message or --around, not both.", 2))
                .Bind(request => Target(arguments.Operands, current).Map(id => request with
                {
                    Id = id
                }));

        private static Result<int?, CliError> MessageNumber(CommandArguments arguments) =>
            arguments.Option("--message").Match(
                value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0
                    ? Result.Success<int?, CliError>(number)
                    : Result.Failure<int?, CliError>(new CliError("--message requires a positive whole number.", 2)),
                () => Result.Success<int?, CliError>(null));

        private static Result<int?, CliError> Offset(CommandArguments arguments) =>
            arguments.Option("--offset").Match(
                value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0
                    ? Result.Success<int?, CliError>(number)
                    : Result.Failure<int?, CliError>(new CliError("--offset requires a nonnegative whole number.", 2)),
                () => Result.Success<int?, CliError>(null));

        // The session to show: the ID given, or with --current the session this agent runs in.
        private Result<string, CliError> Target(ImmutableArray<string> operands, bool current) =>
            Result.Success<ImmutableArray<string>, CliError>(operands)
                .Ensure(rest => !current || rest.Length != 1 || rest[0].StartsWith("--", StringComparison.Ordinal),
                    new CliError("Use either a session ID or --current, not both.", 2))
                .Ensure(rest => rest.Length == (current ? 0 : 1) && !rest.Any(argument => argument.StartsWith("--", StringComparison.Ordinal)),
                    new CliError(CommandLine.Usage, 2))
                .Bind(rest => current ? _currentSession.Require() : Result.Success<string, CliError>(rest[0].Trim()));
    }
}
