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

        public int? MessageNumber { get; init; }

        // Zero-based UTF-16 index in the joined original message.
        public int Offset { get; init; }

        // Neighboring messages shown with each match.
        public int Context { get; init; }

        public int Max { get; init; } = 6;

        public int Chars { get; init; } = 1200;
    }

    /// <summary>Reads indexed sessions back from the corpus: resolves an ID prefix to one session and shows its messages compactly.</summary>
    sealed internal class SessionReader
    {
        private readonly string _root;
        private readonly ExclusionService _exclusions;

        public SessionReader(ClientContext paths, ExclusionService exclusions)
        {
            _root = paths.Root;
            _exclusions = exclusions;
        }

        // Reads one session compactly: its last messages, or the messages matching a pattern clipped around the match.
        public Result<string, CliError> Show(ShowRequest request) =>
            FindSession(request.Id)
                .Bind(session => Attempt.Io(() => _exclusions.LoadChecked()).MapError(error => new CliError(error, 2))
                    .Bind(excluded => ExclusionService.IsExcluded(session.Value.SessionId, [.. excluded])
                        ? Result.Failure<string, CliError>(new CliError("The session is excluded.", 2))
                        : ReadMessages(session.Key, request.MessageNumber is not null).Bind(messages => Excerpts(messages, request)
                            .Map(excerpts => request.MessageNumber is null
                                ? Compact.Plain(Heading(session.Value, messages.Length) + excerpts)
                                : Heading(session.Value, messages.Length) + excerpts))));

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
        private Result<ImmutableArray<Compact.Segment>, CliError> ReadMessages(string name, bool exact) =>
            Attempt.Io(() =>
                {
                    var text = ReadSource(name);
                    return exact ? Transcript.SourceMessages(text)
                        : Compact.Segments(text).GroupBy(segment => segment.Number).Select(group => group.First() with
                    {
                        Text = string.Join('\n', group.Select(segment => segment.Text))
                    }).ToImmutableArray();
                }).MapError(error => new CliError(error, 2));

        internal string ReadSource(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0
                || Path.IsPathRooted(name) || Path.GetFileName(name) != name)
                throw new InvalidDataException("Invalid corpus filename.");
            var corpus = Path.Combine(_root, "corpus");
            if ((File.GetAttributes(corpus) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Corpus directory is a filesystem link.");
            var path = Path.Combine(corpus, name);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Corpus file is a filesystem link.");
            return File.ReadAllText(path);
        }

        private static string Heading(DocumentMeta meta, int count) =>
            Compact.SessionLine(meta.SessionId, meta.Client, $"{count} messages", $"last {Compact.Time(meta.LastActivity)}", meta.Title) + "\n";

        // The last messages; with a pattern, a line counting the matching messages, then the newest of them with their neighbours.
        private static Result<string, CliError> Excerpts(ImmutableArray<Compact.Segment> messages, ShowRequest request) =>
            request.MessageNumber is null && request.Offset != 0
                ? Result.Failure<string, CliError>(new CliError("--offset requires --message.", 2))
                : request.MessageNumber is not null
                ? Exact(messages, request)
                : request.Around is null
                ? Result.Success<string, CliError>(Render(messages, Newest(messages.Length, request.Last), request.Chars))
                : Matching(messages, request.Around).Map(matches =>
                    $"{matches.Length} matching messages{(matches.Length > request.Max ? $"; showing the newest {request.Max}" : "")}\n" +
                    Render(messages, Around(matches, messages.Length, request.Context, request.Max), request.Chars));

        private static Result<string, CliError> Exact(ImmutableArray<Compact.Segment> messages, ShowRequest request)
        {
            if (request.MessageNumber <= 0 || request.Around is not null || request.Offset < 0)
                return new CliError("Give a positive message number without --around.", 2);
            var matches = messages.Select((message, position) => (message, position))
                .Where(item => item.message.Number == request.MessageNumber).Select(item => item.position).Take(2).ToArray();
            if (matches.Length == 0) return new CliError($"Message {request.MessageNumber} was not found in this session.", 2);
            if (matches.Length > 1) return new CliError($"Message {request.MessageNumber} is ambiguous in this session.", 2);
            var index = matches[0];
            var original = messages[index].Text;
            if (request.Offset > 0 && request.Offset >= original.Length) return new CliError("Message offset is beyond the end of the message.", 2);
            var chars = Math.Clamp(request.Chars, 200, 20_000);
            var (start, end) = PageBounds(original, request.Offset, chars);
            var picked = Around([(index, 0)], messages.Length, Math.Clamp(request.Context, 0, 5), 1);
            var output = new StringBuilder();
            foreach (var (position, _) in picked)
            {
                var message = messages[position];
                output.Append(Compact.Label(message)).Append(' ');
                if (position == index)
                {
                    output.Append($"[offset {start}; next offset {(end < original.Length ? end.ToString() : "end")}; total {original.Length} UTF-16 units]\n");
                    if (start > 0) output.Append($"[{start} chars before] ...");
                    output.Append(original[start..end]);
                    if (end < original.Length) output.Append($" ... [+{original.Length - end} chars]");
                }
                else output.Append(Compact.Clip(message.Text, chars));
                output.Append('\n');
            }
            return output.ToString();
        }

        private static (int Start, int End) PageBounds(string text, int offset, int chars)
        {
            var start = offset;
            if (start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1])) start--;
            if (start > 0 && WordChar(text[start - 1]) && WordChar(text[start]))
            {
                var first = start;
                var last = start;
                while (first > 0 && WordChar(text[first - 1])) first--;
                while (last < text.Length && WordChar(text[last])) last++;
                if (last - first <= chars) start = first;
            }
            var end = start + Math.Min(chars, text.Length - start);
            if (end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
            if (end < text.Length && end > start && WordChar(text[end - 1]) && WordChar(text[end]))
            {
                var first = end;
                var last = end;
                while (first > start && WordChar(text[first - 1])) first--;
                while (last < text.Length && WordChar(text[last])) last++;
                if (first > start && last - first <= chars) end = first;
            }
            return (start, end);
        }

        private static bool WordChar(char value) => char.IsLetterOrDigit(value) || value == '_';

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
