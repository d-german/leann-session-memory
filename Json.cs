using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace SessionMemory;

/// <summary>Python-style reads of decoded JSON: missing keys are None and "or" uses truthiness.</summary>
static class Json
{
    public static JsonElement? Get(this JsonElement? value, string name) =>
        value is { ValueKind: JsonValueKind.Object } item && item.TryGetProperty(name, out var child) ? child : null;

    public static bool Truthy(this JsonElement? value) => value is { } item && item.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => item.GetString()!.Length > 0,
        JsonValueKind.Number => !item.TryGetDouble(out var number) || number != 0,
        JsonValueKind.Array => item.GetArrayLength() > 0,
        JsonValueKind.Object => item.EnumerateObject().Any(),
        _ => false,
    };

    public static string Str(this JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.String => value.Value.GetString()!,
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Number or JsonValueKind.Array or JsonValueKind.Object => value.Value.GetRawText(),
        _ => "None",
    };

    public static string StrOr(this JsonElement? value, string fallback) => value.Truthy() ? value.Str() : fallback;

    public static string? AsString(this JsonElement? value) => value is { ValueKind: JsonValueKind.String } item ? item.GetString() : null;

    public static long IntOr(this JsonElement? value, long fallback) => !value.Truthy() ? fallback : value!.Value.ValueKind switch
    {
        JsonValueKind.Number => value.Value.TryGetInt64(out var number) ? number : (long)Math.Truncate(value.Value.GetDouble()),
        JsonValueKind.True => 1,
        JsonValueKind.String => long.Parse(value.Value.GetString()!.Trim(), CultureInfo.InvariantCulture),
        _ => throw new FormatException("Value is not an integer."),
    };

    public static bool PhaseAllowed(this JsonElement? phase) =>
        phase is null or { ValueKind: JsonValueKind.Null } || phase.AsString() is "commentary" or "final" or "final_answer";

    public static string ExtractContent(this JsonElement? content, string allowedType)
    {
        if (content is { ValueKind: JsonValueKind.String } text) return text.GetString()!;
        if (content is not { ValueKind: JsonValueKind.Array } blocks) return "";
        var values = new List<string>();
        foreach (var block in blocks.EnumerateArray())
        {
            var item = (JsonElement?)block;
            if (block.ValueKind == JsonValueKind.Object && item.Get("type").AsString() == allowedType && item.Get("text").AsString() is { } value)
                values.Add(value);
        }
        return string.Join('\n', values);
    }

    public static JsonValueKind Kind(this JsonNode? node) => node?.GetValueKind() ?? JsonValueKind.Null;

    public static bool Truthy(this JsonNode? node) => node.Kind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => node!.GetValue<string>().Length > 0,
        JsonValueKind.Number => !node!.AsValue().TryGetValue<double>(out var number) || number != 0,
        JsonValueKind.Array => node!.AsArray().Count > 0,
        JsonValueKind.Object => node!.AsObject().Count > 0,
        _ => false,
    };

    public static string Str(this JsonNode? node) => node.Kind() switch
    {
        JsonValueKind.String => node!.GetValue<string>(),
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Null => "None",
        _ => node!.ToJsonString(),
    };

    public static string StrOr(this JsonNode? node, string fallback) => node.Truthy() ? node.Str() : fallback;

    public static string? AsString(this JsonNode? node) => node.Kind() == JsonValueKind.String ? node!.GetValue<string>() : null;

    public static string IsoFromMs(this JsonElement? value) => Transcript.IsoFromMs(value?.ValueKind switch
    {
        JsonValueKind.Number => value.Value.GetDouble(),
        JsonValueKind.String => ParseDouble(value.Value.GetString()),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => null,
    });

    public static string IsoFromMs(this JsonNode? node) => Transcript.IsoFromMs(node.Kind() switch
    {
        JsonValueKind.Number => node!.GetValue<double>(),
        JsonValueKind.String => ParseDouble(node!.GetValue<string>()),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => null,
    });

    public static double? ParseDouble(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
}

/// <summary>Python-style conversions of SQLite column values.</summary>
static class Sql
{
    public static bool Truthy(object? value) => value switch
    {
        null => false,
        long number => number != 0,
        double number => number != 0,
        string text => text.Length > 0,
        _ => true,
    };

    public static string Str(object? value) => value switch
    {
        null => "None",
        string text => text,
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    public static string StrOr(object? value, string fallback) => Truthy(value) ? Str(value) : fallback;

    public static long IntOr(object? value, long fallback) => !Truthy(value) ? fallback : value switch
    {
        long number => number,
        double number => (long)Math.Truncate(number),
        string text => long.Parse(text.Trim(), CultureInfo.InvariantCulture),
        _ => fallback,
    };

    public static double? Number(object? value) => value switch
    {
        long number => number,
        double number => number,
        string text => Json.ParseDouble(text),
        _ => null,
    };
}

sealed class JsonlStats
{
    public long Malformed { get; set; }

    public long PartialTrailing { get; set; }
}

/// <summary>Streams JSON Lines, counting malformed records and an unfinished last record separately.</summary>
static class Jsonl
{
    static readonly JsonDocumentOptions Options = new() { MaxDepth = 512 };
    static readonly byte[] ByteOrderMark = [0xEF, 0xBB, 0xBF];

    // Yielded elements are only valid until the enumerator advances.
    public static IEnumerable<JsonElement> Read(string path, JsonlStats stats, params string[] tokens)
    {
        var needles = tokens.Select(token => Encoding.UTF8.GetBytes(token)).ToArray();
        foreach (var (line, partial) in Lines(path))
        {
            if (needles.Length > 0 && !needles.Any(needle => line.Span.IndexOf(needle) >= 0))
            {
                if (partial && !Parses(line)) stats.PartialTrailing++;
                continue;
            }
            using var document = Parse(line);
            if (document is null)
            {
                if (partial) stats.PartialTrailing++;
                else stats.Malformed++;
                continue;
            }
            if (document.RootElement.ValueKind == JsonValueKind.Object) yield return document.RootElement;
        }
    }

    public static IEnumerable<JsonObject> ReadNodes(string path, JsonlStats stats)
    {
        foreach (var (line, partial) in Lines(path))
        {
            var node = ParseNode(line, out var valid);
            if (!valid)
            {
                if (partial) stats.PartialTrailing++;
                else stats.Malformed++;
                continue;
            }
            if (node is JsonObject item) yield return item;
        }
    }

    public static JsonDocument? Parse(string? text)
    {
        if (text is null) return null;
        try
        {
            return JsonDocument.Parse(text, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static bool Parses(ReadOnlyMemory<byte> line)
    {
        using var document = Parse(line);
        return document is not null;
    }

    static JsonDocument? Parse(ReadOnlyMemory<byte> line)
    {
        if (Utf8Json(line) is not { } json) return null;
        try
        {
            return JsonDocument.Parse(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static JsonNode? ParseNode(ReadOnlyMemory<byte> line, out bool valid)
    {
        valid = false;
        if (Utf8Json(line) is not { } json) return null;
        try
        {
            var node = JsonNode.Parse(json.Span, documentOptions: Options);
            valid = true;
            return node;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static ReadOnlyMemory<byte>? Utf8Json(ReadOnlyMemory<byte> line)
    {
        if (line.Span.StartsWith(ByteOrderMark)) line = line[3..];
        return Utf8.IsValid(line.Span) ? line : null;
    }

    // Lines stay valid only until the enumerator advances.
    static IEnumerable<(ReadOnlyMemory<byte> Line, bool Partial)> Lines(string path)
    {
        using var stream = Open(path);
        var buffer = new byte[1 << 16];
        var start = 0;
        var end = 0;
        var endOfFile = false;
        while (true)
        {
            var newline = Array.IndexOf(buffer, (byte)'\n', start, end - start);
            if (newline < 0 && !endOfFile)
            {
                if (start > 0)
                {
                    Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                    end -= start;
                    start = 0;
                }
                if (end == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
                var read = stream.Read(buffer, end, buffer.Length - end);
                if (read == 0) endOfFile = true;
                else end += read;
                continue;
            }
            if (start == end) yield break;
            var lineEnd = newline < 0 ? end : newline + 1;
            var partial = newline < 0 && buffer[lineEnd - 1] != (byte)'\r';
            var line = new ReadOnlyMemory<byte>(buffer, start, lineEnd - start);
            start = lineEnd;
            yield return (line, partial);
        }
    }

    static Stream Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) ? new ZstdSharp.DecompressionStream(file) : file;
    }
}
