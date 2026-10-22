using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SessionMemory;

sealed class Message
{
    public Message(string id, string role, string text, long sequence, string timestamp)
    {
        Id = id;
        Role = role;
        Text = text;
        Sequence = sequence;
        Timestamp = timestamp;
    }

    public string Id { get; }

    public string Role { get; }

    public string Text { get; }

    public long Sequence { get; }

    public string Timestamp { get; }
}

sealed class Document
{
    public Document(string family, string client, string sessionId, string sourceRef, List<Message> messages)
    {
        Family = family;
        Client = client;
        SessionId = sessionId;
        SourceRef = sourceRef;
        Messages = messages;
    }

    public string Family { get; }

    public string Client { get; }

    public string SessionId { get; }

    public string SourceRef { get; }

    public List<Message> Messages { get; }

    // The client's name for the session, such as "Add AI to Protocol Pathway"; empty when it has none.
    public string Title { get; init; } = "";
}

sealed class FamilyResult
{
    public FamilyResult(List<Document> documents, Dictionary<string, long> stats)
    {
        Documents = documents;
        Stats = stats;
    }

    public List<Document> Documents { get; }

    public Dictionary<string, long> Stats { get; }
}

/// <summary>Renders documents exactly like the original Python bridge, so corpus hashes stay comparable.</summary>
static partial class Transcript
{
    static readonly Regex ManyNewlines = MyRegex();
    static readonly Regex UnsafeName = MyRegex1();
    static readonly Regex Whitespace = MyRegex2();

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
    public static List<string> SplitText(string text, int limit = 900)
    {
        var points = CodePoints(text);
        if (points.Length <= limit) return [text];
        var parts = new List<string>();
        var start = 0;
        var end = points.Length;
        while (end - start > limit)
        {
            var cut = Math.Max(LastIndexOf(points, start, limit, '\n'), LastIndexOf(points, start, limit, ' '));
            if (cut < limit / 2) cut = limit;
            var (partStart, partEnd) = Trim(points, start, start + cut);
            parts.Add(FromCodePoints(points.AsSpan(partStart, partEnd - partStart)));
            (start, end) = Trim(points, start + cut, end);
        }
        if (end > start) parts.Add(FromCodePoints(points.AsSpan(start, end - start)));
        return parts;
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
            "chronology: oldest to newest", "",
        ];
        var kept = 0;
        var position = 0;
        foreach (var message in document.Messages.OrderBy(item => item.Sequence).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            position++;
            var text = Normalize(message.Text);
            if (text.Length == 0) continue;
            var parts = SplitText(text);
            for (var part = 0; part < parts.Count; part++)
            {
                lines.AddRange([
                    $"## Message {position:D6} part {part + 1}/{parts.Count}",
                    .. identity,
                    $"message_id: {message.Id}", $"role: {message.Role}",
                    $"timestamp: {message.Timestamp}", $"source_ref: {sourceRef}",
                    "text:", parts[part], "",
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
        try
        {
            var time = DateTime.UnixEpoch.AddSeconds(whole).AddTicks(micros * 10);
            return time.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) + "+00:00";
        }
        catch (ArgumentOutOfRangeException)
        {
            return "";
        }
    }

    // Python's str.isspace() also counts U+001C..U+001F.
    static bool IsSpace(char value) => char.IsWhiteSpace(value) || value is >= '\x1c' and <= '\x1f';

    static bool IsSpace(int point) => point <= char.MaxValue && IsSpace((char)point);

    static string StripEnd(string value)
    {
        var end = value.Length;
        while (end > 0 && IsSpace(value[end - 1])) end--;
        return value[..end];
    }

    static int LastIndexOf(int[] points, int start, int limit, char value)
    {
        for (var index = limit - 1; index >= 0; index--)
        {
            if (points[start + index] == value) return index;
        }
        return -1;
    }

    static (int Start, int End) Trim(int[] points, int start, int end)
    {
        while (start < end && IsSpace(points[start])) start++;
        while (end > start && IsSpace(points[end - 1])) end--;
        return (start, end);
    }

    static int[] CodePoints(string value)
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

    static string FromCodePoints(ReadOnlySpan<int> points)
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
