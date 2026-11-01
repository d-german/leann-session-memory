using CSharpFunctionalExtensions;

namespace SessionMemory.Commands
{
    sealed internal class RelatedCommand : ICommand
    {
        private readonly HistoryNavigation _history;
        public RelatedCommand(HistoryNavigation history) => _history = history;
        public string Verb => "related";
        public bool RequiresArguments => true;

        public int Execute(CommandArguments arguments)
        {
            var json = arguments.Flag("--json");
            var top = arguments.Number("--top", 12);
            var message = arguments.Number("--message", 0);
            var operands = arguments.Operands.ToArray();
            return top
                .Ensure(_ => operands.Length == 1 && !operands[0].StartsWith("--", StringComparison.Ordinal),
                    new CliError("Give one session ID and --message with a positive number.", 2))
                .Bind(n => message.Bind(m => _history.Related(new RelatedRequest(operands[0], m, n, json))))
                .Match(output => CommandLine.Write(output, json), CommandLine.Fail);
        }
    }
}
