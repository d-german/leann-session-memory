using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using SessionMemory.Clients;

namespace SessionMemory
{
    /// <summary>
    /// The session running this command, as its agent host announces it. Each client may name the variable its host sets to the ID of the session
    /// running a command; the variables are read in the clients' registration order and the first that holds a usable ID wins. Hosts that set none
    /// of them make no session current.
    /// </summary>
    sealed internal class CurrentSession
    {
        private const string Undetected = "The current session could not be detected; give its session ID instead.";

        public CurrentSession(IEnumerable<ISessionClient> clients)
        {
            Variables = [.. clients.Select(client => client.SessionVariable).OfType<string>()];
        }

        /// <summary>The variables read, in order of precedence.</summary>
        public ImmutableArray<string> Variables { get; }

        // The ID is matched by prefix, so a value too short to be safe counts as no session.
        public string? Detect() =>
            Variables
                .Select(name => Environment.GetEnvironmentVariable(name)?.Trim())
                .FirstOrDefault(value => value is { Length: >= ExclusionService.MinimumIdLength });

        // --current asks for this session, so it fails when no host announced one.
        public Result<string, CliError> Require() => Maybe.From(Detect()).ToResult(new CliError(Undetected, 2));

        // The one session to look in: with --current the session this agent runs in, otherwise the one given, if any.
        public Result<string?, CliError> Scope(bool current, string? session) =>
            current ? Require().Map(string? (id) => id) : Result.Success<string?, CliError>(session);
    }
}
