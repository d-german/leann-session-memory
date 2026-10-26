using CSharpFunctionalExtensions;

namespace SessionMemory.Commands
{
    /// <summary>search: semantic search over the recent and archive LEANN indexes.</summary>
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
            // --current lets an agent whose context was compacted search its own session.
            return arguments.Number("--top-k", 8)
                .Bind(topK => _currentSession.Scope(current, null).Map(only => new SearchRequest
                {
                    Query = string.Join(' ', arguments.Operands),
                    TopK = Math.Clamp(topK, 1, 50),
                    Json = json,
                    IncludeCurrent = includeCurrent || current,
                    OnlySession = only
                }))
                .Bind(_indexer.Search)
                .Match(output => CommandLine.Write(output, json), CommandLine.Fail);
        }
    }
}
