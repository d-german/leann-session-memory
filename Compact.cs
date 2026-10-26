using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SessionMemory
{
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
        public static List<Segment> Segments(string text)
        {
            var segments = new List<Segment>();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var (number, role, timestamp) = (0, "", "");
            var body = new StringBuilder();
            var header = StartsWithHeader(lines);
            foreach (var line in lines)
            {
                if (MessageHeader.Match(line) is { Success: true } match)
                {
                    Flush();
                    (number, role, timestamp, header) = (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), "", "", true);
                }
                else if (header) (header, role, timestamp) = ReadHeaderLine(line, role, timestamp);
                else if (line.Trim().Length > 0) body.Append(line).Append('\n');
            }
            Flush();
            return segments;

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
        public static string Show(string root, string id, int last, string? around, int context, int max, int chars)
        {
            var (file, meta) = FindSession(root, id);
            var messages = Segments(File.ReadAllText(Path.Combine(root, "corpus", file)))
                .GroupBy(segment => segment.Number)
                .Select(group => group.First() with
                {
                    Text = string.Join('\n', group.Select(segment => segment.Text))
                })
                .ToList();
            var text = new StringBuilder(SessionLine(meta.SessionId, meta.Client, $"{messages.Count} messages", $"last {Time(meta.LastActivity)}", meta.Title)).Append('\n');
            var picked = new SortedDictionary<int, int>();
            if (around is null)
            {
                for (var index = Math.Max(0, messages.Count - last); index < messages.Count; index++) picked[index] = 0;
            }
            else
            {
                var pattern = new Regex(around, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
                var matches = messages.Select((message, index) => (Index: index, Match: pattern.Match(message.Text))).Where(hit => hit.Match.Success).ToList();
                text.Append($"{matches.Count} matching messages{(matches.Count > max ? $"; showing the newest {max}" : "")}\n");
                foreach (var (index, match) in matches.TakeLast(max))
                {
                    for (var neighbor = Math.Max(0, index - context); neighbor <= Math.Min(messages.Count - 1, index + context); neighbor++) picked.TryAdd(neighbor, 0);
                    picked[index] = match.Index;
                }
            }
            foreach (var (index, center) in picked) text.Append(Label(messages[index])).Append(' ').Append(Clip(messages[index].Text, chars, center)).Append('\n');
            return Plain(text.ToString());
        }

        // An ID prefix resolves to one session: an exact ID wins, then the only session that is not a subagent.
        private static KeyValuePair<string, DocumentMeta> FindSession(string root, string id)
        {
            var documents = Store.Load<Manifest>(Path.Combine(root, "state", "manifest.json"))?.Documents ?? [];
            var candidates = documents.Where(pair => pair.Value.SessionId.StartsWith(id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Where(pair => pair.Value.SessionId.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: 1 } exact) candidates = exact;
            else if (candidates.Where(pair => !pair.Value.SessionId.Contains(":subagent:", StringComparison.Ordinal)).ToList() is { Count: 1 } parent) candidates = parent;
            switch (candidates.Count)
            {
                case 0:
                    throw new InvalidOperationException($"No indexed session starts with '{id}'.");
                case > 1:
                    throw new InvalidOperationException("Several sessions match; give more of the ID:\n" +
                                                        string.Join('\n', candidates.Take(10).Select(pair => $"  {pair.Value.SessionId} | {pair.Value.Client} | {pair.Value.Title}")));
            }
            return candidates[0];
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
