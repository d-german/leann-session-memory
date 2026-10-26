using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using SessionMemory.Clients;

namespace SessionMemory
{
    /// <summary>One session to show compactly: its ID or an ID prefix, and which messages to show and how long.</summary>
    sealed internal record ShowRequest
    {
        public required string Id { get; init; }

        public int Last { get; init; } = 10;

        // Shows the messages matching this pattern, clipped around the match, instead of the last ones.
        public string? Around { get; init; }

        // Neighboring messages shown with each match.
        public int Context { get; init; }

        public int Max { get; init; } = 6;

        public int Chars { get; init; } = 1200;
    }

    /// <summary>Reads indexed sessions back from the corpus: resolves an ID prefix to one session and shows its messages compactly.</summary>
    sealed internal class SessionReader
    {
        private readonly string _root;

        public SessionReader(ClientContext paths)
        {
            _root = paths.Root;
        }

        // Reads one session compactly: its last messages, or the messages matching a pattern clipped around the match.
        public Result<string, CliError> Show(ShowRequest request) =>
            FindSession(request.Id)
                .Bind(session => ReadMessages(Path.Combine(_root, "corpus", session.Key))
                    .Bind(messages => Excerpts(messages, request)
                        .Map(excerpts => Compact.Plain(Heading(session.Value, messages.Length) + excerpts))));

        // An ID prefix resolves to one session: an exact ID wins, then the only session that is not a subagent.
        public Result<KeyValuePair<string, DocumentMeta>, CliError> FindSession(string id)
        {
            var documents = Store.Load<Manifest>(Path.Combine(_root, "state", "manifest.json"))?.Documents ?? [];
            var candidates = documents.Where(pair => pair.Value.SessionId.StartsWith(id, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
            return OnlyOne(candidates.Where(pair => pair.Value.SessionId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .Or(() => OnlyOne(candidates.Where(pair => !pair.Value.SessionId.Contains(":subagent:", StringComparison.Ordinal))))
                .Or(() => OnlyOne(candidates))
                .ToResult(() => new CliError(candidates.IsEmpty
                    ? $"No indexed session starts with '{id}'."
                    : "Several sessions match; give more of the ID:\n" +
                      string.Join('\n', candidates.Take(10).Select(pair => $"  {pair.Value.SessionId} | {pair.Value.Client} | {pair.Value.Title}")), 2));
        }

        // The one item, or nothing when there are none or several.
        private static Maybe<T> OnlyOne<T>(IEnumerable<T> items) => items.Take(2).ToArray() is [var only] ? Maybe.From(only) : Maybe<T>.None;

        // The parts of a long message are joined again, so each segment is one whole message.
        private static Result<ImmutableArray<Compact.Segment>, CliError> ReadMessages(string path) =>
            Attempt.Io(() => File.ReadAllText(path))
                .MapError(error => new CliError(error, 2))
                .Map(text => Compact.Segments(text)
                    .GroupBy(segment => segment.Number)
                    .Select(group => group.First() with
                    {
                        Text = string.Join('\n', group.Select(segment => segment.Text))
                    })
                    .ToImmutableArray());

        private static string Heading(DocumentMeta meta, int count) =>
            Compact.SessionLine(meta.SessionId, meta.Client, $"{count} messages", $"last {Compact.Time(meta.LastActivity)}", meta.Title) + "\n";

        // The last messages; with a pattern, a line counting the matching messages, then the newest of them with their neighbours.
        private static Result<string, CliError> Excerpts(ImmutableArray<Compact.Segment> messages, ShowRequest request) =>
            request.Around is null
                ? Result.Success<string, CliError>(Render(messages, Newest(messages.Length, request.Last), request.Chars))
                : Matching(messages, request.Around).Map(matches =>
                    $"{matches.Length} matching messages{(matches.Length > request.Max ? $"; showing the newest {request.Max}" : "")}\n" +
                    Render(messages, Around(matches, messages.Length, request.Context, request.Max), request.Chars));

        // Message index -> the position its clip centres on; the last messages are shown from their start.
        private static SortedDictionary<int, int> Newest(int count, int last)
        {
            var picked = new SortedDictionary<int, int>();
            for (var index = Math.Max(0, count - last); index < count; index++) picked[index] = 0;
            return picked;
        }

        // The newest matches are clipped around the match and their neighbours from their start.
        private static SortedDictionary<int, int> Around(ImmutableArray<(int Index, int Position)> matches, int count, int context, int max)
        {
            var picked = new SortedDictionary<int, int>();
            foreach (var (index, position) in matches.TakeLast(max))
            {
                for (var neighbor = Math.Max(0, index - context); neighbor <= Math.Min(count - 1, index + context); neighbor++) picked.TryAdd(neighbor, 0);
                picked[index] = position;
            }
            return picked;
        }

        // The pattern comes from the command line: a syntax error, or a match running longer than two seconds, is reported rather than thrown.
        private static Result<ImmutableArray<(int Index, int Position)>, CliError> Matching(ImmutableArray<Compact.Segment> messages, string around)
        {
            try
            {
                var pattern = new Regex(around, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
                return messages.Select((message, index) => (Index: index, Match: pattern.Match(message.Text)))
                    .Where(hit => hit.Match.Success)
                    .Select(hit => (hit.Index, Position: hit.Match.Index))
                    .ToImmutableArray();
            }
            catch (Exception error) when (error is ArgumentException or RegexMatchTimeoutException)
            {
                return new CliError(error.Message, 2);
            }
        }

        private static string Render(ImmutableArray<Compact.Segment> messages, SortedDictionary<int, int> picked, int chars)
        {
            var text = new StringBuilder();
            foreach (var (index, center) in picked)
                text.Append(Compact.Label(messages[index])).Append(' ').Append(Compact.Clip(messages[index].Text, chars, center)).Append('\n');
            return text.ToString();
        }
    }
}
