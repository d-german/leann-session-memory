using CSharpFunctionalExtensions;

namespace SessionMemory.Commands
{
    sealed internal class TimelineCommand : ICommand
    {
        private readonly HistoryNavigation _history;
        public TimelineCommand(HistoryNavigation history) => _history = history;
        public string Verb => "timeline";
        public bool RequiresArguments => true;

        public int Execute(CommandArguments arguments)
        {
            var json = arguments.Flag("--json");
            var includeCurrent = arguments.Flag("--include-current");
            var cursor = arguments.Option("--cursor");
            var top = arguments.Number("--top", 12);
            var terms = arguments.Operands.ToArray();
            return top
                .Ensure(_ => !terms.Any(t => t.StartsWith("--", StringComparison.Ordinal)), new CliError("Unknown or incomplete timeline option.", 2))
                .Ensure(_ => cursor.HasNoValue || !cursor.Value.StartsWith("--", StringComparison.Ordinal), new CliError("Give a timeline cursor.", 2))
                .Bind(n => _history.Timeline(new TimelineRequest(terms, n, cursor.GetValueOrDefault(), includeCurrent, json)))
                .Match(output => CommandLine.Write(output, json), CommandLine.Fail);
        }
    }
}
