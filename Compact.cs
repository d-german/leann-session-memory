using System.Collections.Immutable;
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
