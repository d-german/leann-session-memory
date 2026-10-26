using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace SessionMemory
{
    /// <summary>One session to show compactly: its ID or an ID prefix, and which messages to show and how long.</summary>
    sealed internal record ShowRequest
    {
        public required string Root { get; init; }

        public required string Id { get; init; }

        public int Last { get; init; } = 10;

        // Shows the messages matching this pattern, clipped around the match, instead of the last ones.
        public string? Around { get; init; }

        // Neighbouring messages shown with each match.
        public int Context { get; init; }

        public int Max { get; init; } = 6;

        public int Chars { get; init; } = 1200;
    }

    /// <summary>
    /// Turns transcript Markdown into compact messages. The per-message headers (client, session, title, ids, source)
    /// are about half of every transcript; agents get them once per session instead of once per message.
    /// </summary>
    internal static partial class Compact
    {
        public sealed record Segment(int Number, string Role, string Timestamp, string Text);

        private static readonly string[] Keys = ["client", "session_id", "title", "message_id", "role", "timestamp", "source_ref", "chronology"];
        private static readonly Regex MessageHeader = MyRegex();
        private static readonly Dictionary<char, string> Ascii = new()
        {
            ['\u2018'] = "'",
            ['\u2019'] = "'",
            ['\u201C'] = "\"",
            ['\u201D'] = "\"",
            ['\u2013'] = "-",
            ['\u2014'] = "-",
            ['\u2026'] = "...",
            ['\u2192'] = "->",
            ['\u2190'] = "<-",
            ['\u00D7'] = "x",
            ['\u2022'] = "*",
            ['\u00A0'] = " ",
            ['\u2264'] = "<=",
            ['\u2265'] = ">=",
            ['\u2260'] = "!=",
            ['\u2713'] = "v",
            ['\u2705'] = "[ok]",
            ['\u274C'] = "[x]"
        };

        // Works on a whole transcript or on a search passage, which may start or end inside a header.
        public static ImmutableArray<Segment> Segments(string text)
        {
            var segments = ImmutableArray.CreateBuilder<Segment>();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var (number, role, timestamp) = (0, "", "");
            var body = new StringBuilder();
            var header = StartsWithHeader(lines);
            foreach (var line in lines)
            {
                // \d also matches digits of other scripts; a heading whose number is not a plain int is ordinary text.
                if (MessageHeader.Match(line) is { Success: true } match
                    && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                {
                    Flush();
                    (number, role, timestamp, header) = (parsed, "", "", true);
                }
                else if (header) (header, role, timestamp) = ReadHeaderLine(line, role, timestamp);
                else if (line.Trim().Length > 0) body.Append(line).Append('\n');
            }
            Flush();
            return segments.ToImmutable();

            void Flush()
            {
                if (body.Length > 0) segments.Add(new Segment(number, role, timestamp, body.ToString().TrimEnd()));
                body.Clear();
            }
        }

        // "[#412 assistant 2026-04-30 15:12]"; "[...]" marks text continuing from a message that starts before the passage.
        public static string Label(Segment segment) =>
            segment.Role.Length == 0
                ? "[...]"
                : "[" + string.Join(' ', new[]
                {
                    segment.Number > 0 ? $"#{segment.Number}" : "", segment.Role, Time(segment.Timestamp)
                }.Where(part => part.Length > 0)) + "]";

        public static string SessionLine(params string?[] parts) =>
            "== " + string.Join(" | ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

        public static string Time(string timestamp) =>
            timestamp.Length >= 16 && timestamp[10] == 'T' ? $"{timestamp[..10]} {timestamp[11..16]}" : timestamp;

        // Typographic punctuation becomes ASCII and symbols or emoji are dropped; letters in other scripts are kept.
        public static string Plain(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (var character in text)
            {
                if (character <= 0x7F) builder.Append(character);
                else if (Ascii.TryGetValue(character, out var replacement)) builder.Append(replacement);
                else if (char.IsLetterOrDigit(character)) builder.Append(character);
            }
            return builder.ToString();
        }

        // UTF-8 whatever the console code page is.
        public static void Write(string text)
        {
            using var output = Console.OpenStandardOutput();
            output.Write(Encoding.UTF8.GetBytes(text));
        }

        // Keeps a window of the text, a third of it before the given position.
        public static string Clip(string text, int chars, int center = 0)
        {
            if (text.Length <= chars) return text;
            var start = Math.Clamp(center - chars / 3, 0, text.Length - chars);
            var end = start + chars;
            return (start > 0 ? $"[{start:N0} chars before] ..." : "") + text[start..end] + $" ... [+{text.Length - end:N0} chars]";
        }

        // Reads one session compactly: its last messages, or the messages matching a pattern clipped around the match.
        public static Result<string, CliError> Show(ShowRequest request) =>
            FindSession(request.Root, request.Id)
                .Bind(session => ReadMessages(Path.Combine(request.Root, "corpus", session.Key))
                    .Bind(messages => Excerpts(messages, request)
                        .Map(excerpts => Plain(Heading(session.Value, messages.Length) + excerpts))));

        // An ID prefix resolves to one session: an exact ID wins, then the only session that is not a subagent.
        public static Result<KeyValuePair<string, DocumentMeta>, CliError> FindSession(string root, string id)
        {
            var documents = Store.Load<Manifest>(Path.Combine(root, "state", "manifest.json"))?.Documents ?? [];
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
        private static Result<ImmutableArray<Segment>, CliError> ReadMessages(string path) =>
            Attempt.Io(() => File.ReadAllText(path))
                .MapError(error => new CliError(error, 2))
                .Map(text => Segments(text)
                    .GroupBy(segment => segment.Number)
                    .Select(group => group.First() with
                    {
                        Text = string.Join('\n', group.Select(segment => segment.Text))
                    })
                    .ToImmutableArray());

        private static string Heading(DocumentMeta meta, int count) =>
            SessionLine(meta.SessionId, meta.Client, $"{count} messages", $"last {Time(meta.LastActivity)}", meta.Title) + "\n";

        // The last messages; with a pattern, a line counting the matching messages, then the newest of them with their neighbours.
        private static Result<string, CliError> Excerpts(ImmutableArray<Segment> messages, ShowRequest request) =>
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
        private static Result<ImmutableArray<(int Index, int Position)>, CliError> Matching(ImmutableArray<Segment> messages, string around)
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

        private static string Render(ImmutableArray<Segment> messages, SortedDictionary<int, int> picked, int chars)
        {
            var text = new StringBuilder();
            foreach (var (index, center) in picked) text.Append(Label(messages[index])).Append(' ').Append(Clip(messages[index].Text, chars, center)).Append('\n');
            return text.ToString();
        }

        // A whole transcript starts with a header, and a passage can start inside one. Later headers follow a message heading.
        private static bool StartsWithHeader(string[] lines)
        {
            var line = lines[0];
            return IsKeyLine(line) || line == "text:" || line == "# Local session transcript"
                   || lines.Length > 1 && IsHeaderFragment(line, lines[1]);
        }

        // "text:" ends a header; its role and timestamp lines label the message.
        private static (bool Header, string Role, string Timestamp) ReadHeaderLine(string line, string role, string timestamp) => line switch
        {
            "text:" => (false, role, timestamp),
            _ when line.StartsWith("role: ", StringComparison.Ordinal) => (true, line[6..], timestamp),
            _ when line.StartsWith("timestamp: ", StringComparison.Ordinal) => (true, role, line[11..]),
            _ => (true, role, timestamp)
        };

        private static bool IsKeyLine(string line) => Keys.Any(key => line.StartsWith(key + ": ", StringComparison.Ordinal));

        // A passage can start partway through a header line; the next line is then another header line.
        private static bool IsHeaderFragment(string line, string next)
        {
            if (!IsKeyLine(next) && next != "text:") return false;
            var match = Fragment().Match(line);
            return !line.Contains(": ", StringComparison.Ordinal) || match.Success && Keys.Any(key => key.EndsWith(match.Groups[1].Value, StringComparison.Ordinal));
        }

        [GeneratedRegex(@"^## Message (\d+) part \d+/\d+$", RegexOptions.Compiled)]
        private static partial Regex MyRegex();

        [GeneratedRegex("^([a-z_]*): ")]
        private static partial Regex Fragment();
    }
}
