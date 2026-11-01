using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SessionMemory
{
    sealed internal record Message
    {
        public Message(string id, string role, string text, long sequence, string timestamp)
        {
            Id = id;
            Role = role;
            Text = text;
            Sequence = sequence;
            Timestamp = timestamp;
        }

        public string Id { get; init; }

        public string Role { get; init; }

        public string Text { get; init; }

        public long Sequence { get; init; }

        public string Timestamp { get; init; }
    }

    sealed internal record Document
    {
        public Document(string family, string client, string sessionId, string sourceRef, ImmutableArray<Message> messages)
        {
            Family = family;
            Client = client;
            SessionId = sessionId;
            SourceRef = sourceRef;
            Messages = messages;
        }

        public string Family { get; init; }

        public string Client { get; init; }

        public string SessionId { get; init; }

        public string SourceRef { get; init; }

        public ImmutableArray<Message> Messages { get; init; }

        // The client's name for the session, such as "Add AI to Protocol Pathway"; empty when it has none.
        public string Title { get; init; } = "";
    }

    sealed internal record FamilyResult
    {
        public FamilyResult(ImmutableArray<Document> documents, Dictionary<string, long> stats)
        {
            Documents = documents;
            Stats = stats;
        }

        public ImmutableArray<Document> Documents { get; init; }

        // Insertion order is the order the manifest lists the counters in, so this stays a Dictionary.
        public Dictionary<string, long> Stats { get; init; }
    }

    /// <summary>Writes readable transcripts with explicit message lengths and reads their original message boundaries.</summary>
    internal static partial class Transcript
    {
        // Ticks and whole seconds from the Unix epoch to DateTime.MaxValue; a later time has no DateTime, so it gives no timestamp.
        private static readonly long MaxEpochTicks = DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks;
        private static readonly long MaxEpochSeconds = MaxEpochTicks / TimeSpan.TicksPerSecond;
        private static readonly Regex ManyNewlines = MyRegex();
        private static readonly Regex UnsafeName = MyRegex1();
        private static readonly Regex Whitespace = MyRegex2();
        private static readonly Regex MessageHeader = new(@"(?m)^## Message ([0-9]+) part ([0-9]+)/([0-9]+)[ \t]*$", RegexOptions.CultureInvariant);

        public static string Normalize(string value)
        {
            value = value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\0", "");
            value = string.Join('\n', value.Split('\n').Select(StripEnd));
            return Strip(ManyNewlines.Replace(value, "\n\n\n"));
        }

        public static string Strip(string value)
        {
            var start = 0;
            var end = value.Length;
            while (start < end && IsSpace(value[start])) start++;
            while (end > start && IsSpace(value[end - 1])) end--;
            return value[start..end];
        }

        // Python slices by code point, so a surrogate pair counts as one character.
        public static ImmutableArray<string> SplitText(string text, int limit = 900)
        {
            var points = CodePoints(text);
            if (points.Length <= limit) return [text];
            var parts = ImmutableArray.CreateBuilder<string>();
            var start = 0;
            var end = points.Length;
            while (end - start > limit)
            {
                var cut = Math.Max(LastIndexOf(points, start, limit, '\n'), LastIndexOf(points, start, limit, ' '));
                if (cut < limit / 2) cut = limit;
                if (cut < limit) cut++;
                parts.Add(FromCodePoints(points.AsSpan(start, cut)));
                start += cut;
            }
            if (end > start) parts.Add(FromCodePoints(points.AsSpan(start, end - start)));
            return parts.DrainToImmutable();
        }

        public static string CleanTitle(string title)
        {
            title = Whitespace.Replace(title, " ").Trim();
            return title.Length > 200 ? title[..200] : title;
        }

        public static (string Content, int Kept, string SourceRef) Render(Document document)
        {
            var sourceRef = Normalize(document.SourceRef);
            // Every passage carries the title, so a search for it finds the session wherever a passage starts.
            var title = CleanTitle(document.Title);
            List<string> identity = [$"client: {document.Client}", $"session_id: {document.SessionId}"];
            if (title.Length > 0) identity.Add($"title: {title}");
            List<string> lines =
            [
                "# Local session transcript", "", .. identity, $"source_ref: {sourceRef}",
                "chronology: oldest to newest", ""
            ];
            var kept = 0;
            var position = 0;
            foreach (var message in document.Messages.OrderBy(item => item.Sequence).ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                position++;
                var text = Normalize(message.Text);
                if (text.Length == 0) continue;
                var parts = SplitText(text);
                for (var part = 0; part < parts.Length; part++)
                {
                    lines.AddRange([
                        $"## Message {position:D6} part {part + 1}/{parts.Length}",
                        .. identity,
                        $"message_id: {message.Id}", $"role: {message.Role}",
                        $"timestamp: {message.Timestamp}", $"source_ref: {sourceRef}",
                        $"text_length: {parts[part].Length}", "text:", parts[part], ""
                    ]);
                }
                kept++;
            }
            return (StripEnd(string.Join('\n', lines)) + "\n", kept, sourceRef);
        }

        public static string DocumentName(Document document)
        {
            var digest = Sha256($"{document.Client}\0{document.SessionId}")[..24];
            var name = UnsafeName.Replace(document.Client.ToLowerInvariant(), "-").Trim('-');
            name = name.Length > 30 ? name[..30] : name;
            return $"{(name.Length == 0 ? "session" : name)}-{digest}.md";
        }

        public static string Sha256(string value) => Sha256(Encoding.UTF8.GetBytes(value));

        public static string Sha256(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

        public static string IsoFromMs(double? milliseconds)
        {
            if (milliseconds is not { } value || !double.IsFinite(value) || value < 0) return "";
            var seconds = value / 1000;
            var whole = Math.Floor(seconds);
            var micros = (long)Math.Round((seconds - whole) * 1_000_000, MidpointRounding.ToEven);
            // Checked before the conversion to ticks, so a huge value cannot overflow it.
            if (whole > MaxEpochSeconds) return "";
            var ticks = (long)whole * TimeSpan.TicksPerSecond + micros * 10;
            return ticks > MaxEpochTicks ? "" : DateTime.UnixEpoch.AddTicks(ticks).ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) + "+00:00";
        }

        // Python's str.isspace() also counts U+001C..U+001F.
        private static bool IsSpace(char value) => char.IsWhiteSpace(value) || value is >= '\x1c' and <= '\x1f';

        private static bool IsSpace(int point) => point <= char.MaxValue && IsSpace((char)point);

        private static string StripEnd(string value)
        {
            var end = value.Length;
            while (end > 0 && IsSpace(value[end - 1])) end--;
            return value[..end];
        }

        private static int LastIndexOf(int[] points, int start, int limit, char value)
        {
            for (var index = limit - 1; index >= 0; index--)
            {
                if (points[start + index] == value) return index;
            }
            return -1;
        }

        // Lengths frame new transcripts; legacy transcripts retain their original newline-joined parts.
        internal static ImmutableArray<Compact.Segment> SourceMessages(string text)
        {
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            var headers = MessageHeader.Matches(text);
            var messages = ImmutableArray.CreateBuilder<Compact.Segment>();
            var previousPart = 0;
            var previousParts = 0;
            var previousFramed = false;
            for (var i = 0; i < headers.Count; i++)
            {
                var header = headers[i];
                if (!int.TryParse(header.Groups[1].Value, out var number)
                    || !int.TryParse(header.Groups[2].Value, out var part)
                    || !int.TryParse(header.Groups[3].Value, out var parts)) continue;
                var start = header.Index + header.Length;
                if (start < text.Length && text[start] == '\n') start++;
                var end = i + 1 < headers.Count ? headers[i + 1].Index : text.Length;
                var marker = text.IndexOf("text:\n", start, end - start, StringComparison.Ordinal);
                if (marker < 0 || marker > start && text[marker - 1] != '\n') continue;
                var role = "";
                var stamp = "";
                int? length = null;
                foreach (var line in text[start..marker].Split('\n'))
                {
                    if (line.StartsWith("role: ", StringComparison.Ordinal)) role = line[6..];
                    else if (line.StartsWith("timestamp: ", StringComparison.Ordinal)) stamp = line[11..];
                    else if (line.StartsWith("text_length: ", StringComparison.Ordinal))
                    {
                        if (!int.TryParse(line.AsSpan(13), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                            throw new InvalidDataException("Invalid transcript message length.");
                        length = value;
                    }
                }
                var bodyStart = marker + 6;
                if (length is { } count)
                {
                    if (count > text.Length - bodyStart) throw new InvalidDataException("Truncated transcript message.");
                    end = bodyStart + count;
                    if (end < text.Length && text[end] != '\n') throw new InvalidDataException("Invalid transcript message boundary.");
                    while (i + 1 < headers.Count && headers[i + 1].Index < end) i++;
                }
                var body = length is null ? text[bodyStart..end].TrimEnd('\n') : text[bodyStart..end];
                if (messages.Count > 0 && part > 1 && part == previousPart + 1 && parts == previousParts
                    && messages[^1] is var previous && previous.Number == number && previous.Role == role && previous.Timestamp == stamp)
                    messages[^1] = previous with { Text = previous.Text + (length is not null && previousFramed ? "" : "\n") + body };
                else messages.Add(new Compact.Segment(number, role, stamp, body));
                (previousPart, previousParts, previousFramed) = (part, parts, length is not null);
            }
            return messages.ToImmutable();
        }

        private static int[] CodePoints(string value)
        {
            var points = new List<int>(value.Length);
            for (var index = 0; index < value.Length; index++)
            {
                if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    points.Add(char.ConvertToUtf32(value[index], value[index + 1]));
                    index++;
                }
                else
                {
                    points.Add(value[index]);
                }
            }
            return [.. points];
        }

        private static string FromCodePoints(ReadOnlySpan<int> points)
        {
            var builder = new StringBuilder(points.Length);
            foreach (var point in points)
            {
                if (point > char.MaxValue) builder.Append(char.ConvertFromUtf32(point));
                else builder.Append((char)point);
            }
            return builder.ToString();
        }

        [GeneratedRegex("\n{4,}", RegexOptions.Compiled)]
        private static partial Regex MyRegex();
        [GeneratedRegex("[^a-z0-9]+", RegexOptions.Compiled)]
        private static partial Regex MyRegex1();
        [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
        private static partial Regex MyRegex2();
    }
}
