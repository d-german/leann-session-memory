using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace SessionMemory
{
    /// <summary>Python-style reads of decoded JSON: missing keys are None and "or" uses truthiness.</summary>
    internal static class Json
    {
        extension(JsonElement? value)
        {
            public JsonElement? Get(string name) =>
                value is { ValueKind: JsonValueKind.Object } item && item.TryGetProperty(name, out var child) ? child : null;

            public bool Truthy() => value is { } item && item.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => item.GetString()!.Length > 0,
                JsonValueKind.Number => !item.TryGetDouble(out var number) || number != 0,
                JsonValueKind.Array => item.GetArrayLength() > 0,
                JsonValueKind.Object => item.EnumerateObject().Any(),
                _ => false
            };

            public string Str() => value?.ValueKind switch
            {
                JsonValueKind.String => value.Value.GetString()!,
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                JsonValueKind.Number or JsonValueKind.Array or JsonValueKind.Object => value.Value.GetRawText(),
                _ => "None"
            };

            public string StrOr(string fallback) => value.Truthy() ? value.Str() : fallback;

            public string? AsString() => value is { ValueKind: JsonValueKind.String } item ? item.GetString() : null;

            public long IntOr(long fallback) => !value.Truthy() ? fallback : value!.Value switch
            {
                { ValueKind: JsonValueKind.Number } number => number.TryGetInt64(out var whole) ? whole : (long)Math.Truncate(number.GetDouble()),
                { ValueKind: JsonValueKind.True } => 1,
                { ValueKind: JsonValueKind.String } text => ParseInteger(text.GetString()!),
                // Truthy() leaves only non-empty objects and arrays here, which Python's int() rejects as well.
                _ => throw new FormatException("Value is not an integer.")
            };

            public bool PhaseAllowed() =>
                value is null or { ValueKind: JsonValueKind.Null } || value.AsString() is "commentary" or "final" or "final_answer";

            public string ExtractContent(string allowedType)
            {
                if (value is { ValueKind: JsonValueKind.String } text) return text.GetString()!;
                if (value is not { ValueKind: JsonValueKind.Array } blocks) return "";
                var values = new List<string>();
                foreach (var block in blocks.EnumerateArray())
                {
                    var item = (JsonElement?)block;
                    if (block.ValueKind == JsonValueKind.Object && item.Get("type").AsString() == allowedType && item.Get("text").AsString() is { } blockText)
                        values.Add(blockText);
                }
                return string.Join('\n', values);
            }

            public string IsoFromMs() => Transcript.IsoFromMs(value?.ValueKind switch
            {
                JsonValueKind.Number => value.Value.GetDouble(),
                JsonValueKind.String => ParseDouble(value.Value.GetString()),
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                _ => null
            });
        }

        extension(JsonNode? node)
        {
            public JsonValueKind Kind() => node?.GetValueKind() ?? JsonValueKind.Null;

            public bool Truthy() => node.Kind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => node!.GetValue<string>().Length > 0,
                JsonValueKind.Number => !node!.AsValue().TryGetValue<double>(out var number) || number != 0,
                JsonValueKind.Array => node!.AsArray().Count > 0,
                JsonValueKind.Object => node!.AsObject().Count > 0,
                _ => false
            };

            public string Str() => node.Kind() switch
            {
                JsonValueKind.String => node!.GetValue<string>(),
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                JsonValueKind.Null => "None",
                _ => node!.ToJsonString()
            };

            public string StrOr(string fallback) => node.Truthy() ? node.Str() : fallback;

            public string? AsString() => node.Kind() == JsonValueKind.String ? node!.GetValue<string>() : null;

            public string IsoFromMs() => Transcript.IsoFromMs(node.Kind() switch
            {
                JsonValueKind.Number => node!.GetValue<double>(),
                JsonValueKind.String => ParseDouble(node!.GetValue<string>()),
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                _ => null
            });
        }

        public static double? ParseDouble(string? text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

        // Text that is not a whole number still fails the family: long.Parse raises the same FormatException (or OverflowException) as before.
        private static long ParseInteger(string text)
        {
            var trimmed = text.Trim();
            return long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : long.Parse(trimmed, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Python-style conversions of SQLite column values.</summary>
    internal static class Sql
    {
        public static bool Truthy(object? value) => value switch
        {
            null => false,
            long number => number != 0,
            double number => number != 0,
            string text => text.Length > 0,
            _ => true
        };

        public static string Str(object? value) => value switch
        {
            null => "None",
            string text => text,
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };

        public static string StrOr(object? value, string fallback) => Truthy(value) ? Str(value) : fallback;

        public static long IntOr(object? value, long fallback) => !Truthy(value) ? fallback : value switch
        {
            long number => number,
            double number => (long)Math.Truncate(number),
            string text => long.Parse(text.Trim(), CultureInfo.InvariantCulture),
            _ => fallback
        };

        public static double? Number(object? value) => value switch
        {
            long number => number,
            double number => number,
            string text => Json.ParseDouble(text),
            _ => null
        };
    }

    sealed internal class JsonlStats
    {
        public long Malformed { get; set; }

        public long PartialTrailing { get; set; }
    }

    /// <summary>Streams JSON Lines, counting malformed records and an unfinished last record separately.</summary>
    internal static class Jsonl
    {
        private static readonly JsonDocumentOptions Options = new()
        {
            MaxDepth = 512
        };

        private static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

        // Yielded elements are only valid until the enumerator advances.
        public static IEnumerable<JsonElement> Read(string path, JsonlStats stats, params string[] tokens)
        {
            var needles = tokens.Select(token => Encoding.UTF8.GetBytes(token)).ToArray();
            foreach (var (line, partial) in Lines(path))
            {
                using var document = ParseMatching(line, partial, needles, stats);
                if (document?.RootElement.ValueKind == JsonValueKind.Object) yield return document.RootElement;
            }
        }

        public static IEnumerable<JsonObject> ReadNodes(string path, JsonlStats stats)
        {
            foreach (var (line, partial) in Lines(path))
            {
                var node = ParseNode(line, out var valid);
                if (!valid)
                {
                    CountInvalid(stats, partial);
                    continue;
                }
                if (node is JsonObject item) yield return item;
            }
        }

        public static JsonDocument? Parse(string? text) =>
            text is not null && TryParse(text, static json => JsonDocument.Parse(json, Options), out var document) ? document : null;

        // Parses a line that contains any of the needles (every line when there are none) and counts lines that fail to parse.
        private static JsonDocument? ParseMatching(ReadOnlyMemory<byte> line, bool partial, byte[][] needles, JsonlStats stats)
        {
            if (needles.Length > 0 && !ContainsAny(line.Span, needles))
            {
                if (partial && !Parses(line)) stats.PartialTrailing++;
                return null;
            }
            var document = Parse(line);
            if (document is null) CountInvalid(stats, partial);
            return document;
        }

        // A loop rather than Any(lambda): the lambda would capture the line and allocate a closure for every line.
        private static bool ContainsAny(ReadOnlySpan<byte> line, byte[][] needles)
        {
            foreach (var needle in needles)
                if (line.IndexOf(needle) >= 0) return true;
            return false;
        }

        private static void CountInvalid(JsonlStats stats, bool partial)
        {
            if (partial) stats.PartialTrailing++;
            else stats.Malformed++;
        }

        private static bool Parses(ReadOnlyMemory<byte> line)
        {
            using var document = Parse(line);
            return document is not null;
        }

        private static JsonDocument? Parse(ReadOnlyMemory<byte> line) =>
            Utf8Json(line) is { } json && TryParse(json, static utf8 => JsonDocument.Parse(utf8, Options), out var document) ? document : null;

        // JSON null is valid, so validity is reported separately from the node.
        private static JsonNode? ParseNode(ReadOnlyMemory<byte> line, out bool valid)
        {
            valid = false;
            if (Utf8Json(line) is not { } json) return null;
            valid = TryParse(json, static utf8 => JsonNode.Parse(utf8.Span, documentOptions: Options), out var node);
            return node;
        }

        // The single JSON boundary: the parsers have no Try variant, and a JsonException means the input is not valid JSON.
        // The parse delegates are static lambdas, which the compiler caches, so a call allocates nothing beyond the parse itself.
        private static bool TryParse<TInput, TOutput>(TInput input, Func<TInput, TOutput> parse, out TOutput? output)
        {
            try
            {
                output = parse(input);
                return true;
            }
            catch (JsonException)
            {
                output = default;
                return false;
            }
        }

        private static ReadOnlyMemory<byte>? Utf8Json(ReadOnlyMemory<byte> line)
        {
            if (line.Span.StartsWith(ByteOrderMark)) line = line[3..];
            return Utf8.IsValid(line.Span) ? line : null;
        }

        // Lines stay valid only until the enumerator advances.
        private static IEnumerable<(ReadOnlyMemory<byte> Line, bool Partial)> Lines(string path)
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
                    endOfFile = !Fill(stream, ref buffer, ref start, ref end);
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

        // Moves the unread bytes to the front, doubles a full buffer and reads more; returns false at the end of the stream.
        private static bool Fill(Stream stream, ref byte[] buffer, ref int start, ref int end)
        {
            if (start > 0)
            {
                Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                end -= start;
                start = 0;
            }
            if (end == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
            var read = stream.Read(buffer, end, buffer.Length - end);
            if (read == 0) return false;
            end += read;
            return true;
        }

        private static Stream Open(string path)
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            return path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) ? new ZstdSharp.DecompressionStream(file) : file;
        }
    }
}
