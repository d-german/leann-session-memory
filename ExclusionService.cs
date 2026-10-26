using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using SessionMemory.Clients;

namespace SessionMemory
{
    /// <summary>A checked change to the exclusion list and the lines that report it; nothing is saved or printed yet.</summary>
    sealed internal record ExclusionUpdate
    {
        public required ImmutableArray<string> Exclusions { get; init; }

        public required ImmutableArray<string> Report { get; init; }
    }

    /// <summary>
    /// The sessions left out of the indexes and searches. An exclusion is a session ID or a prefix of at least eight characters, so it also covers
    /// the session's Claude subagents; the canary that validates every index build can never be excluded.
    /// </summary>
    sealed internal class ExclusionService
    {
        // IDs are matched by prefix, so a shorter one could match unrelated sessions.
        public const int MinimumIdLength = 8;

        private readonly string _root;
        private readonly CurrentSession _currentSession;

        public ExclusionService(ClientContext paths, CurrentSession currentSession)
        {
            _root = paths.Root;
            _currentSession = currentSession;
        }

        public string FilePath => Path.Combine(_root, "state", "excluded.json");

        public List<string> Load() => Store.Load<List<string>>(FilePath) ?? [];

        // The session running a search only echoes the question, so searches leave it out unless asked not to.
        // The ID is matched like any exclusion, so a short value must not drop unrelated sessions.
        public ImmutableArray<string> ForSearch(bool includeCurrent, string? currentSession = null)
        {
            var exclusions = Load();
            currentSession ??= _currentSession.Detect();
            if (!includeCurrent && currentSession?.Trim() is { Length: >= MinimumIdLength } id) exclusions.Add(id);
            return [.. exclusions];
        }

        // A prefix also covers Claude subagent sessions, whose IDs start with the parent's.
        public static bool IsExcluded(string sessionId, ImmutableArray<string> exclusions) =>
            exclusions.Any(prefix => sessionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        // Takes a full session ID or a prefix of at least 8 characters, as shown in search results. Nothing is saved until Save.
        public Result<ExclusionUpdate, CliError> Exclude(string id) => Checked(id).Bind(Add);

        public Result<ExclusionUpdate, CliError> Include(string id) => Checked(id).Bind(Remove);

        public void Save(ExclusionUpdate update) => Store.Save(FilePath, update.Exclusions);

        private static Result<string, CliError> Checked(string id) =>
            Result.Success<string, CliError>(id.Trim())
                .Ensure(trimmed => trimmed.Length >= MinimumIdLength, new CliError("Give at least 8 characters of the session ID.", 2))
                .Ensure(trimmed => !Indexer.CanaryId.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase),
                    new CliError("The canary session validates every index build and cannot be excluded.", 2));

        private Result<ExclusionUpdate, CliError> Add(string id)
        {
            var documents = Store.Load<Manifest>(Path.Combine(_root, "state", "manifest.json"))?.Documents.Values ?? Enumerable.Empty<DocumentMeta>();
            ImmutableArray<string> prefix = [id];
            var matches = documents.Where(document => IsExcluded(document.SessionId, prefix)).ToImmutableArray();
            var exclusions = Load();
            return Result.Success<ImmutableArray<DocumentMeta>, CliError>(matches)
                .Ensure(found => !found.IsEmpty, new CliError($"No indexed session starts with '{id}'.", 1))
                .Map(found => new ExclusionUpdate
                {
                    Exclusions = exclusions.Contains(id, StringComparer.OrdinalIgnoreCase) ? [.. exclusions] : [.. exclusions, id],
                    Report = [.. found.Select(match => $"Excluded {match.SessionId} ({match.Client}) {match.Title}")]
                });
        }

        private Result<ExclusionUpdate, CliError> Remove(string id)
        {
            var exclusions = Load();
            var kept = exclusions.Where(entry => !entry.Equals(id, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
            return Result.Success<ImmutableArray<string>, CliError>(kept)
                .Ensure(rest => rest.Length < exclusions.Count, new CliError($"'{id}' is not excluded.", 1))
                .Map(rest => new ExclusionUpdate
                {
                    Exclusions = rest,
                    Report = [$"Included {id} again."]
                });
        }
    }
}
