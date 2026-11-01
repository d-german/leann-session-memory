using CSharpFunctionalExtensions;

namespace SessionMemory.Commands
{
    /// <summary>Search cited conversation windows using lexical and semantic retrieval.</summary>
    sealed internal class SearchCommand : ICommand
    {
        private readonly Indexer _indexer;
        private readonly CurrentSession _currentSession;

        public SearchCommand(Indexer indexer, CurrentSession currentSession)
        {
            _indexer = indexer;
            _currentSession = currentSession;
        }

        public string Verb => "search";

        public bool RequiresArguments => true;

        public int Execute(CommandArguments arguments)
        {
            var json = arguments.Flag("--json");
            var includeCurrent = arguments.Flag("--include-current");
            var current = arguments.Flag("--current");
            var latest = arguments.Flag("--latest");
            var session = arguments.Option("--session");
            var excludedTopic = arguments.Option("--exclude-topic");
            var limit = arguments.Number("--top-k", 8);
            var operands = arguments.Operands.Where(term => !string.IsNullOrWhiteSpace(term)).ToArray();
            var unknown = operands.FirstOrDefault(term => term.StartsWith("--", StringComparison.Ordinal));
            // --current lets an agent whose context was compacted search its own session.
            return limit
                .Ensure(_ => unknown is null, new CliError($"Unknown or incomplete option '{unknown}'.\n{CommandLine.Usage}", 2))
                .Ensure(_ => operands.Length > 0, new CliError("Give a question to search for.", 2))
                .Ensure(_ => !current || session.HasNoValue, new CliError("Use either --current or --session, not both.", 2))
                .Ensure(_ => session.HasNoValue || session.Value.Trim().Length >= 8 && !session.Value.StartsWith("--", StringComparison.Ordinal), new CliError("Give at least eight characters of the session ID.", 2))
                .Ensure(_ => excludedTopic.HasNoValue || !string.IsNullOrWhiteSpace(excludedTopic.Value), new CliError("Give a literal phrase for --exclude-topic.", 2))
                .Bind(topK => _currentSession.Scope(current, session.Map(value => value.Trim()).GetValueOrDefault()).Map(only => new SearchRequest
                {
                    Query = string.Join(' ', operands),
                    TopK = Math.Clamp(topK, 1, 50),
                    Latest = latest,
                    Json = json,
                    IncludeCurrent = includeCurrent || current || session.HasValue,
                    OnlySession = only,
                    ExcludedTopic = excludedTopic.GetValueOrDefault()
                }))
                .Bind(_indexer.Search)
                .Match(output => CommandLine.Write(output, json), CommandLine.Fail);
        }
    }
}
