using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpFunctionalExtensions;
using SessionMemory.Clients;

namespace SessionMemory
{
    sealed internal record TimelineRequest(string[] Terms, int Top = 12, string? Cursor = null, bool IncludeCurrent = false, bool Json = false);
    sealed internal record RelatedRequest(string SessionId, int Message, int Top = 12, bool Json = false);

    /// <summary>Browse dated original messages and explicit references without inferring their relevance.</summary>
    sealed internal class HistoryNavigation
    {
        private const int Budget = 11999;
        private const string Warning = "Matches are mentions in the indexed snapshot; relevance and global recency are not established.";
        private readonly string _root;
        private readonly ExclusionService _exclusions;

        public HistoryNavigation(ClientContext paths, ExclusionService exclusions)
        {
            _root = paths.Root;
            _exclusions = exclusions;
        }

        public Result<string, CliError> Timeline(TimelineRequest request)
        {
            if (request.Terms.Length is < 1 or > 8 || request.Terms.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 256)
                || request.Top is < 1 or > 50 || request.Cursor?.Length > 2048)
                return new CliError("Give 1 to 8 nonempty literal terms of at most 256 characters and a top value from 1 to 50.", 2);
            return Run(request.IncludeCurrent, (db, docs, unavailable, excluded) => TimelineCore(db, docs, unavailable, excluded, request));
        }

        public Result<string, CliError> Related(RelatedRequest request)
        {
            if (request.SessionId.Trim().Length < 8 || request.SessionId.Length > 256 || request.Message <= 0 || request.Top is < 1 or > 50)
                return new CliError("Give a session ID of at least eight characters, a positive message number, and a top value from 1 to 50.", 2);
            return Run(false, (db, docs, unavailable, _) => RelatedCore(db, docs, unavailable, request));
        }

        private Result<string, CliError> Run(bool includeCurrent, Func<SqliteReader, Dictionary<string, DocumentMeta>, int, IReadOnlyList<string>, string> action)
        {
            try
            {
                var state = Path.Combine(_root, "state");
                if ((File.GetAttributes(state) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid cache directory.");
                var manifestPath = Path.Combine(state, "manifest.json");
                if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid manifest file.");
                var manifestText = File.ReadAllText(manifestPath);
                using var manifestJson = JsonDocument.Parse(manifestText);
                if (manifestJson.RootElement.ValueKind != JsonValueKind.Object
                    || !manifestJson.RootElement.TryGetProperty("documents", out var documentMap)
                    || documentMap.ValueKind != JsonValueKind.Object) throw new IOException("Cannot read the session manifest.");
                var manifest = JsonSerializer.Deserialize<Manifest>(manifestText, Store.Options)
                               ?? throw new IOException("Cannot read the session manifest.");
                var stored = _exclusions.LoadChecked();
                var documents = manifest.Documents;
                if (documents is null || documents.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null || string.IsNullOrWhiteSpace(p.Value.SessionId)))
                    throw new IOException("Cannot read the session manifest.");
                var indexable = Indexer.Visible(documents, [.. stored], null);
                var visible = Indexer.Visible(documents, _exclusions.ForSearch(includeCurrent, stored: stored), null);
                ConversationSearch.Prepare(_root, documents, indexable);
                var cache = Path.Combine(_root, "state", "conversation-search.sqlite");
                if (!File.Exists(cache)) return new CliError("No indexed conversations are available.", 2);
                if ((File.GetAttributes(cache) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid cache file.");
                using var db = new SqliteReader(cache);
                db.Execute("CREATE TEMP TABLE visible(name TEXT PRIMARY KEY)");
                db.Execute("BEGIN");
                var cached = db.Query("SELECT name,sha FROM files").ToDictionary(r => (string)r[0]!, r => (string)r[1]!, StringComparer.Ordinal);
                var checkedDocs = documents.Where(p => visible.Contains(p.Key) && cached.TryGetValue(p.Key, out var sha)
                    && sha.Equals(p.Value.Sha256, StringComparison.OrdinalIgnoreCase)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                foreach (var file in checkedDocs.Keys) db.Execute("INSERT INTO visible VALUES (?)", file);
                var result = action(db, checkedDocs, visible.Count - checkedDocs.Count, stored);
                db.Execute("COMMIT");
                if (File.ReadAllText(manifestPath) != manifestText
                    || !_exclusions.LoadChecked().SequenceEqual(stored, StringComparer.Ordinal)
                    || !visible.SetEquals(Indexer.Visible(documents, _exclusions.ForSearch(includeCurrent, stored: stored), null)))
                    throw new IOException("Session visibility changed during navigation; retry.");
                return result;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or JsonException or FormatException or OverflowException)
            {
                return new CliError(error.Message, 2);
            }
        }

        private static string TimelineCore(SqliteReader db, Dictionary<string, DocumentMeta> docs, int unavailable, IReadOnlyList<string> excluded, TimelineRequest request)
        {
            var folded = request.Terms.Select(ConversationSearch.FoldNavigationText).ToArray();
            var predicate = string.Join(" OR ", folded.Select(_ => "instr(m.folded,?)>0"));
            var parameters = folded.Cast<object?>().ToArray();
            var from = " FROM visible v CROSS JOIN nav_messages m ON m.file=v.name WHERE (" + predicate + ")";
            var stats = db.Query("SELECT count(*),sum(CASE WHEN m.utc_ticks IS NULL THEN 1 ELSE 0 END)" + from, parameters).Single();
            var total = Convert.ToInt64(stats[0], CultureInfo.InvariantCulture);
            var undated = stats[1] is null ? 0 : Convert.ToInt64(stats[1], CultureInfo.InvariantCulture);
            var fingerprint = Transcript.Sha256(JsonSerializer.Serialize(new
            {
                terms = folded,
                excluded = excluded.OrderBy(x => x, StringComparer.Ordinal),
                request.IncludeCurrent,
                sources = docs.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { p.Key, p.Value.Sha256, p.Value.SessionId, p.Value.Client, p.Value.Title }),
                unavailable
            }));
            var offset = ReadCursor(request.Cursor, fingerprint, total);
            var rows = db.Query("SELECT m.file,m.number,m.role,m.stamp,m.utc_ticks,m.text" + from
                + " ORDER BY m.utc_ticks IS NULL,m.utc_ticks DESC,m.file,m.number,m.role,m.stamp LIMIT ? OFFSET ?",
                [.. parameters, request.Top, offset]).ToArray();
            var output = new JsonObject
            {
                ["operation"] = "timeline", ["warning"] = Warning, ["total_matches"] = total,
                ["matching"] = "Literal substrings after invariant uppercase; Unicode normalization is not applied.",
                ["dated"] = total - undated, ["undated"] = undated, ["unavailable_sources"] = unavailable,
                ["output_truncated"] = false, ["next_cursor"] = null, ["items"] = new JsonArray()
            };
            var items = (JsonArray)output["items"]!;
            foreach (var row in rows)
            {
                items.Add(Citation(db, row, docs, LiteralPosition((string)row[5]!, request.Terms), true));
                output["next_cursor"] = offset + items.Count < total ? Cursor(fingerprint, offset + items.Count) : null;
                if (Render(output, request.Json).Length <= Budget) continue;
                items.RemoveAt(items.Count - 1);
                output["output_truncated"] = true;
                break;
            }
            output["next_cursor"] = offset + items.Count < total ? Cursor(fingerprint, offset + items.Count) : null;
            if (items.Count == 0 && total > offset && rows.Length > 0) throw new IOException("A result cannot fit within the output limit.");
            return Render(output, request.Json);
        }

        private static string RelatedCore(SqliteReader db, Dictionary<string, DocumentMeta> docs, int unavailable, RelatedRequest request)
        {
            var matches = docs.Where(p => p.Value.SessionId.StartsWith(request.SessionId.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            var exact = matches.Where(p => p.Value.SessionId.Equals(request.SessionId.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exact.Length == 1) matches = exact;
            else if (matches.Count(p => !p.Value.SessionId.Contains(":subagent:", StringComparison.Ordinal)) == 1)
                matches = matches.Where(p => !p.Value.SessionId.Contains(":subagent:", StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1) throw new IOException("Source session is unavailable or ambiguous.");
            var source = db.Query("SELECT m.file,m.number,m.role,m.stamp,m.utc_ticks,m.text,m.id FROM visible v CROSS JOIN nav_messages m ON m.file=v.name WHERE m.file=? AND m.number=?",
                matches[0].Key, request.Message).ToArray();
            if (source.Length != 1) throw new IOException("Source message is unavailable or ambiguous.");
            var sourceId = Convert.ToInt64(source[0][6], CultureInfo.InvariantCulture);
            const string targets = " FROM nav_refs s JOIN nav_refs r ON r.target=s.target"
                + " JOIN nav_messages m ON m.id=r.message_id JOIN visible v ON v.name=m.file"
                + " WHERE s.message_id=? AND m.id<>? GROUP BY m.id";
            var total = Convert.ToInt64(db.Query("SELECT count(*) FROM (SELECT m.id" + targets + ")", sourceId, sourceId).Single()[0], CultureInfo.InvariantCulture);
            var rows = db.Query("SELECT m.file,m.number,m.role,m.stamp,m.utc_ticks,m.text,min(s.target)" + targets
                + " ORDER BY m.utc_ticks IS NULL,m.utc_ticks DESC,m.file,m.number,m.role,m.stamp LIMIT ?", sourceId, sourceId, request.Top).ToArray();
            var sourceText = (string)source[0][5]!;
            var sessionTargets = docs.Values.Where(m => SessionPosition(sourceText, m.SessionId) is not null
                    && !m.SessionId.Equals(matches[0].Value.SessionId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.SessionId, StringComparer.Ordinal)
                .Select(m => new JsonObject { ["session_id"] = Clip(m.SessionId, 256), ["client"] = Clip(m.Client, 100),
                    ["title"] = Clip(m.Title, 150), ["read_hint"] = "search <topic> --session " + m.SessionId }).ToArray();
            var sourceReference = rows.Length > 0 ? ("uri", (string)rows[0][6]!)
                : sessionTargets.Length > 0 ? ("session", sessionTargets[0]["session_id"]!.GetValue<string>())
                : ((string, string)?)null;
            var sourcePosition = sourceReference is { } reference
                ? reference.Item1 == "session" ? SessionPosition(sourceText, reference.Item2)
                    : ReferencePosition(sourceText, reference.Item2) : null;
            var output = new JsonObject
            {
                ["operation"] = "related", ["warning"] = "Shared references are navigation hints, not proof of related work.",
                ["source"] = Citation(db, source[0], docs, sourcePosition, sourceReference is not null),
                ["total_uri_messages"] = total, ["total_sessions"] = sessionTargets.Length,
                ["unavailable_sources"] = unavailable, ["output_truncated"] = false, ["more_results"] = false,
                ["items"] = new JsonArray()
            };
            var items = (JsonArray)output["items"]!;
            foreach (var row in rows)
            {
                var citation = Citation(db, row, docs, ReferencePosition((string)row[5]!, (string)row[6]!), true);
                citation["reference"] = Clip((string)row[6]!, 600);
                citation["kind"] = "uri";
                items.Add(citation);
                if (Render(output, request.Json).Length <= Budget) continue;
                items.RemoveAt(items.Count - 1);
                output["output_truncated"] = true;
                break;
            }
            foreach (var session in sessionTargets.Take(Math.Max(0, request.Top - items.Count)))
            {
                session["kind"] = "session";
                items.Add(session);
                if (Render(output, request.Json).Length <= Budget) continue;
                items.RemoveAt(items.Count - 1);
                output["output_truncated"] = true;
                break;
            }
            output["more_results"] = items.Count < total + sessionTargets.Length;
            if (items.Count == 0 && total + sessionTargets.Length > 0) throw new IOException("A result cannot fit within the output limit.");
            return Render(output, request.Json);
        }

        private static JsonObject Citation(SqliteReader db, object?[] row, Dictionary<string, DocumentMeta> docs, (int Index, int Length)? position, bool expected)
        {
            var meta = docs[(string)row[0]!];
            var number = Convert.ToInt64(row[1], CultureInfo.InvariantCulture);
            var ticks = row[4] is null ? (long?)null : Convert.ToInt64(row[4], CultureInfo.InvariantCulture);
            var unique = (long)db.Query("SELECT count(*) FROM nav_messages WHERE file=? AND number=?", row[0], number).Single()[0]! == 1;
            var hint = unique ? $"show {meta.SessionId} --message {number} --context 1 --chars 6000" : null;
            if (hint is not null && position is { Index: > 0 } match) hint += $" --offset {Math.Max(0, match.Index - 200)}";
            return new JsonObject
            {
                ["session_id"] = Clip(meta.SessionId, 256), ["client"] = Clip(meta.Client, 100), ["title"] = Clip(meta.Title, 150),
                ["message"] = number, ["role"] = Clip((string)row[2]!, 80), ["original_timestamp"] = Clip((string)row[3]!, 100),
                ["timestamp_utc"] = ticks is null ? null : new DateTimeOffset(ticks.Value, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture),
                ["excerpt"] = ClipAround((string)row[5]!, position),
                ["position_warning"] = !unique ? "Message number is ambiguous; exact-message hint omitted."
                    : expected && position is null ? "Match position unavailable; excerpt starts at the beginning." : null,
                ["read_hint"] = hint
            };
        }

        private static (int Index, int Length)? LiteralPosition(string text, IEnumerable<string> terms)
        {
            (int Index, int Length)? best = null;
            foreach (var term in terms)
            {
                var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (index < 0 || index + term.Length > text.Length
                    || ConversationSearch.FoldNavigationText(text.Substring(index, term.Length)) != ConversationSearch.FoldNavigationText(term)) continue;
                if (best is null || index < best.Value.Index) best = (index, term.Length);
            }
            return best;
        }

        private static (int Index, int Length)? ReferencePosition(string text, string target)
        {
            foreach (var index in ConversationSearch.NavigationUriPositions(text, target))
                return (index, Math.Min(target.Length, text.Length - index));
            return null;
        }

        private static (int Index, int Length)? SessionPosition(string text, string target)
        {
            static bool IdCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or ':';
            var offset = 0;
            while (offset <= text.Length - target.Length)
            {
                var index = text.IndexOf(target, offset, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                var end = index + target.Length;
                if ((index == 0 || !IdCharacter(text[index - 1])) && (end == text.Length || !IdCharacter(text[end])))
                    return (index, target.Length);
                offset = index + 1;
            }
            return null;
        }

        private static string ClipAround(string text, (int Index, int Length)? position)
        {
            if (position is null || text.Length <= 450) return Clip(text, 450);
            var (index, length) = position.Value;
            var start = Math.Max(0, index - 100);
            while (start > 0 && start < index && !char.IsWhiteSpace(text[start - 1])) start++;
            if (start > 0 && char.IsLowSurrogate(text[start])) start--;
            var end = Math.Min(text.Length, Math.Max(start + 450, index + Math.Min(length, 300)));
            while (end < text.Length && end > index + Math.Min(length, 300) && !char.IsWhiteSpace(text[end])) end--;
            if (end < text.Length && end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
            return (start > 0 ? "[earlier text omitted] ... " : "") + text[start..end].Trim()
                   + (end < text.Length ? " ... [later text omitted]" : "");
        }

        private static string Clip(string text, int max)
        {
            if (text.Length <= max) return text;
            var end = max;
            while (end > max / 2 && !char.IsWhiteSpace(text[end])) end--;
            if (end <= max / 2) end = max;
            if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
            return text[..end].TrimEnd() + " ... [omitted]";
        }

        private sealed record CursorData(string Fingerprint, long Offset);
        private static string Cursor(string fingerprint, long offset) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new CursorData(fingerprint, offset))));

        private static long ReadCursor(string? cursor, string fingerprint, long total)
        {
            if (cursor is null) return 0;
            var data = JsonSerializer.Deserialize<CursorData>(Convert.FromBase64String(cursor));
            if (data is null || data.Fingerprint != fingerprint || data.Offset < 0 || data.Offset >= total)
                throw new IOException("Invalid or stale timeline cursor.");
            return data.Offset;
        }

        private static string Render(JsonObject output, bool json)
        {
            if (json) return output.ToJsonString();
            var text = new StringBuilder();
            foreach (var property in output.Where(p => p.Key != "items"))
                text.Append(property.Key).Append(": ").Append(property.Value is null ? "" : property.Value.ToString()).Append('\n');
            foreach (var item in (JsonArray)output["items"]!)
                text.Append(item!.ToJsonString()).Append('\n');
            return text.ToString().TrimEnd();
        }
    }
}
