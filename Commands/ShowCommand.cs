using System.Collections.Immutable;
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

        // Reads --last, --context, --max and --chars in this order, so the first bad number is the one reported, then --around and the session.
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
                .Map(request => request with
                {
                    Around = arguments.Option("--around").GetValueOrDefault()
                })
                .Bind(request => Target(arguments.Operands, current).Map(id => request with
                {
                    Id = id
                }));

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
