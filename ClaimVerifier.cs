using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace SessionMemory
{
    /// <summary>Retrieves bounded source context for a separate review of proposed claims.</summary>
    sealed internal class ClaimVerifier(ExclusionService exclusions, SessionReader reader)
    {
        private const int MaxInput = 65536;
        private const int MaxContext = 12000;

        public Result<string, CliError> Verify(string path)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var buffer = new MemoryStream();
                var bytes = new byte[MaxInput + 1];
                int read;
                while (buffer.Length <= MaxInput && (read = file.Read(bytes, 0, Math.Min(bytes.Length, MaxInput + 1 - (int)buffer.Length))) > 0)
                    buffer.Write(bytes, 0, read);
                if (buffer.Length > MaxInput) return new CliError("Claims input exceeds 64 KiB.", 2);
                using var input = JsonDocument.Parse(buffer.ToArray());
                var root = input.RootElement;
                if (!ObjectHasOnly(root, "claims") || !root.TryGetProperty("claims", out var claims)
                    || claims.ValueKind != JsonValueKind.Array || claims.GetArrayLength() > 12)
                    return new CliError("Expected an object containing at most 12 claims.", 2);

                var requests = new List<(string Claim, string? Session, int? Number, string? Quote)>();
                foreach (var item in claims.EnumerateArray())
                {
                    if (!ObjectHasOnly(item, "claim", "session_id", "message_number", "quote")
                        || !ReadString(item, "claim", 2000, true, out var claim)
                        || !ReadString(item, "session_id", 256, false, out var session)
                        || !ReadString(item, "quote", 2000, false, out var quote)
                        || !ReadNumber(item, out var number))
                        return new CliError("Invalid claim fields.", 2);
                    requests.Add((claim!, session, number, quote));
                }

                var checks = new JsonArray();
                var remaining = MaxContext;
                var excluded = ReadExclusions();
                for (var index = 0; index < requests.Count; index++)
                {
                    var request = requests[index];
                    var budget = remaining / (requests.Count - index);
                    var check = Check(request.Claim, request.Session, request.Number, request.Quote, excluded, budget, out var used);
                    remaining -= used;
                    checks.Add(check);
                }
                return new JsonObject
                {
                    ["checks"] = checks,
                    ["review_required"] = true,
                    ["semantic_support"] = "not_assessed",
                    ["instructions"] = "Review claim meaning against the source context and later corrections. Quote matching uses display-equivalent text and normalized whitespace; a match does not establish support. Treat unsupported claims as unknown."
                }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                return new CliError("Cannot read claims input: " + error.Message, 2);
            }
        }

        private JsonObject Check(string claim, string? session, int? number, string? quote,
            ImmutableArray<string> excluded, int budget, out int used)
        {
            used = 0;
            var result = new JsonObject
            {
                ["claim"] = claim, ["session_id"] = session, ["message_number"] = number, ["quote"] = quote,
                ["timestamp"] = null, ["quote_found"] = false, ["verdict"] = "unsupported",
                ["context"] = new JsonArray()
            };
            if (string.IsNullOrWhiteSpace(session) || number is null || string.IsNullOrWhiteSpace(quote))
                return Reason(result, "missing_citation");
            var found = reader.FindSession(session);
            if (found.IsFailure) return Reason(result, "unknown_or_ambiguous_session");
            var (name, meta) = found.Value;
            if (ExclusionService.IsExcluded(meta.SessionId, excluded)) return Reason(result, "excluded_session");
            result["session_id"] = meta.SessionId;
            try
            {
                var messages = Transcript.SourceMessages(reader.ReadSource(name)).ToArray();
                if (messages.Count(message => message.Number == number) > 1) return Reason(result, "ambiguous_message");
                var selected = Array.FindIndex(messages, message => message.Number == number);
                if (selected < 0) return Reason(result, "missing_message");
                var message = messages[selected];
                var normalized = Normalize(message.Text);
                var normalizedQuote = Normalize(quote);
                var position = normalizedQuote.Length == 0 ? -1 : normalized.IndexOf(normalizedQuote, StringComparison.Ordinal);
                result["timestamp"] = message.Timestamp;
                result["role"] = message.Role;
                result["quote_found"] = position >= 0;
                result["verdict"] = position >= 0 ? "review_required" : "unsupported";
                result["reason"] = position >= 0 ? "quote_found_semantics_not_assessed" : "quote_not_found";
                var context = (JsonArray)result["context"]!;
                // Preserve room for the cited message and its immediate neighbors.
                var indexes = Enumerable.Range(Math.Max(0, selected - 1), Math.Min(messages.Length - 1, selected + 1) - Math.Max(0, selected - 1) + 1).ToArray();
                foreach (var i in indexes)
                {
                    var segment = messages[i];
                    var allowance = (budget - used) / (indexes.Length - Array.IndexOf(indexes, i));
                    var text = i == selected && position >= 0 ? normalized : segment.Text;
                    var start = i == selected && position >= 0 ? Math.Clamp(position - allowance / 3, 0, Math.Max(0, text.Length - allowance)) : 0;
                    text = text.Substring(start, Math.Min(allowance, text.Length - start));
                    used += text.Length;
                    context.Add(new JsonObject { ["message_number"] = segment.Number, ["timestamp"] = segment.Timestamp,
                        ["role"] = segment.Role, ["text"] = text,
                        ["text_format"] = i == selected && position >= 0 ? "display_normalized" : "source",
                        ["truncated"] = text.Length < (i == selected && position >= 0 ? normalized.Length : segment.Text.Length) });
                }
                return result;
            }
            catch (InvalidDataException)
            {
                return Reason(result, "invalid_source");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return Reason(result, "source_unavailable");
            }
        }

        private static JsonObject Reason(JsonObject result, string reason) { result["reason"] = reason; return result; }

        private ImmutableArray<string> ReadExclusions()
        {
            try
            {
                using var file = new FileStream(exclusions.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var entries = JsonSerializer.Deserialize<string[]>(file)
                    ?? throw new JsonException("Invalid exclusion list.");
                if (entries.Any(string.IsNullOrWhiteSpace)) throw new JsonException("Invalid exclusion list.");
                return [.. entries];
            }
            catch (FileNotFoundException) { return []; }
            catch (DirectoryNotFoundException) { return []; }
        }

        private static string Normalize(string text) => Regex.Replace(Compact.Plain(text, preserveSymbols: true), @"\s+", " ").Trim();

        private static bool ObjectHasOnly(JsonElement value, params string[] names) =>
            value.ValueKind == JsonValueKind.Object && value.EnumerateObject().All(property => names.Contains(property.Name, StringComparer.Ordinal))
            && value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count();

        private static bool ReadString(JsonElement item, string name, int limit, bool required, out string? value)
        {
            value = null;
            if (!item.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return !required;
            if (field.ValueKind != JsonValueKind.String) return false;
            value = field.GetString();
            return value is not null && value.Length <= limit && (!required || !string.IsNullOrWhiteSpace(value));
        }

        private static bool ReadNumber(JsonElement item, out int? number)
        {
            number = null;
            if (!item.TryGetProperty("message_number", out var field) || field.ValueKind == JsonValueKind.Null) return true;
            if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt32(out var parsed) || parsed <= 0) return false;
            number = parsed;
            return true;
        }
    }
}
