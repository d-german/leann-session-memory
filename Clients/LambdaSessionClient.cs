using System.Collections.Immutable;

namespace SessionMemory.Clients
{
    /// <summary>
    /// An ISessionClient whose behavioris supplied as delegates instead of a dedicated class; used for the synthetic client.
    /// </summary>
    sealed internal class LambdaSessionClient : ISessionClient
    {
        private readonly Func<ClientContext, ImmutableArray<string>> _sources;
        private readonly Func<ClientContext, FamilyResult> _parse;
        private readonly Func<FindContext, IEnumerable<Action<Finder.Scan>>> _findWork;

        // Without find work the client is captured but find never scans it; without a session variable it never names the current session.
        public LambdaSessionClient(string name, Func<ClientContext, ImmutableArray<string>> sources, Func<ClientContext, FamilyResult> parse,
            Func<FindContext, IEnumerable<Action<Finder.Scan>>>? findWork = null, string? sessionVariable = null)
        {
            Name = name;
            SessionVariable = sessionVariable;
            _sources = sources;
            _parse = parse;
            _findWork = findWork ?? (_ => []);
        }

        public string Name { get; }

        public string? SessionVariable { get; }

        public ImmutableArray<string> Sources(ClientContext context) => _sources(context);

        public FamilyResult Parse(ClientContext context) => _parse(context);

        public IEnumerable<Action<Finder.Scan>> FindWork(FindContext context) => _findWork(context);
    }
}
