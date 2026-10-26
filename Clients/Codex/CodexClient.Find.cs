using System.Text.Json;

namespace SessionMemory.Clients.Codex
{
    // Searches the Codex history database for find, including tool inputs that never reach the transcripts.
    sealed internal partial class CodexClient
    {
        // Only what Codex was asked to do; command output stays out like other tool outputs.
        public static string ItemInput(string itemType, string itemJson)
        {
            using var json = Jsonl.Parse(itemJson);
            var item = json?.RootElement;
            return itemType switch
            {
                "commandExecution" => item.Get("command").Str(),
                "mcpToolCall" => $"{item.Get("server").Str()} {item.Get("tool").Str()} {item.Get("arguments").Str()}",
                "fileChange" => item.Get("changes").Str(),
                "webSearch" => item.Get("query").Str(),
                _ => itemJson
            };
        }

        // The Codex table is one 1 GB scan; row ranges let it run in parallel like the files.
        private static IEnumerable<Action<Finder.Scan>> HistoryWork(string home)
        {
            var history = History(home);
            if (!File.Exists(history)) yield break;
            var (first, last) = RowRange(history);
            if (last < first) yield break;
            const int parts = 32;
            var size = (last - first) / parts + 1;
            for (var start = first; start <= last; start += size)
            {
                var (from, to) = (start, Math.Min(last, start + size - 1));
                yield return scan => ScanRows(scan, history, from, to);
            }
        }

        private static (long First, long Last) RowRange(string history)
        {
            using var database = new SqliteReader(history);
            var row = database.Query("SELECT min(rowid), max(rowid) FROM thread_items").First();
            return (Sql.IntOr(row[0], 1), Sql.IntOr(row[1], 0));
        }

        private static void ScanRows(Finder.Scan scan, string history, long from, long to)
        {
            using var database = new SqliteReader(history);
            var filter = string.Join(" OR ", scan.Terms.SelectMany(term => Finder.Scan.Forms(term)).Select(term => $"item_json LIKE '%{Finder.LikeEscape(term)}%' ESCAPE '\\'"));
            var query = $"SELECT thread_id, item_type, created_at_ms, item_json FROM thread_items WHERE rowid BETWEEN {from} AND {to} AND item_type IN " +
                        $"('userMessage','agentMessage','commandExecution','mcpToolCall','fileChange','webSearch') AND ({filter})";
            foreach (var row in database.Query(query))
            {
                var itemType = Sql.Str(row[1]);
                var itemJson = Sql.Str(row[3]);
                scan.Note("codex", Sql.Str(row[0]), ItemInput(itemType, itemJson), Finder.Utc(Transcript.IsoFromMs(Sql.Number(row[2]))), () =>
                {
                    using var json = Jsonl.Parse(itemJson);
                    if (json is null) return null;
                    var item = json.RootElement;
                    var input = itemType switch
                    {
                        "commandExecution" => item.TryGetProperty("command", out var command) ? command : (JsonElement?)null,
                        "mcpToolCall" => item.TryGetProperty("arguments", out var arguments) ? arguments : null,
                        "fileChange" => item.TryGetProperty("changes", out var changes) ? changes : null,
                        "webSearch" => item.TryGetProperty("query", out var search) ? search : null,
                        _ => item
                    };
                    var label = itemType == "mcpToolCall" ? $"{((JsonElement?)item).Get("tool").Str()}" : itemType;
                    return input is { } element ? Finder.Excerpt(element, label, scan.Terms) : null;
                });
            }
        }
    }
}
