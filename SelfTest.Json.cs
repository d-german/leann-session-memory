using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SessionMemory
{
    internal static partial class SelfTest
    {
        private const string JsonNotAnInteger = "FormatException: Value is not an integer.";

        private static void JsonRefactorChecks()
        {
            JsonIntOrChecks();
            JsonParseChecks();
            var temporary = Directory.CreateTempSubdirectory("session-memory-json-test-");
            try
            {
                JsonlCountChecks(temporary.FullName);
                JsonlFilterChecks(temporary.FullName);
                SqliteReaderChecks(temporary.FullName);
            }
            finally
            {
                temporary.Delete(recursive: true);
            }
        }

        private static void JsonIntOrChecks()
        {
            using var document = JsonDocument.Parse("""
                {"n":42,"f":3.9,"g":-3.9,"t":true,"no":false,"zero":0,"s":" 17 ","empty":"","nil":null,"o":{"x":1},"eo":{},"a":[1],"ea":[],
                 "bad":"abc","decimal":"1.5","huge":"99999999999999999999"}
                """);
            JsonElement? root = document.RootElement;
            Check(root.Get("n").IntOr(0) == 42 && root.Get("f").IntOr(0) == 3 && root.Get("g").IntOr(0) == -3 && root.Get("t").IntOr(0) == 1
                  && root.Get("s").IntOr(0) == 17, "converts truthy JSON values like int()");
            string[] falsy = ["missing", "no", "zero", "empty", "nil", "eo", "ea"];
            Check(falsy.All(name => root.Get(name).IntOr(7) == 7), "falls back for falsy JSON values");
            Check(JsonFailure(() => root.Get("o").IntOr(0)) == JsonNotAnInteger && JsonFailure(() => root.Get("a").IntOr(0)) == JsonNotAnInteger,
                "rejects non-empty objects and arrays as integers");
            var bad = JsonFailure(() => root.Get("bad").IntOr(0));
            var fraction = JsonFailure(() => root.Get("decimal").IntOr(0));
            Check(bad.StartsWith("FormatException: ", StringComparison.Ordinal) && bad == JsonFailure(() => long.Parse("abc", CultureInfo.InvariantCulture))
                  && fraction == JsonFailure(() => long.Parse("1.5", CultureInfo.InvariantCulture)), "keeps long.Parse's FormatException text for non-numeric strings");
            var huge = JsonFailure(() => root.Get("huge").IntOr(0));
            Check(huge.StartsWith("OverflowException: ", StringComparison.Ordinal)
                  && huge == JsonFailure(() => long.Parse("99999999999999999999", CultureInfo.InvariantCulture)), "keeps the overflow failure for huge strings");
        }

        private static void JsonParseChecks()
        {
            using var parsed = Jsonl.Parse("""{"a":1}""");
            using var literal = Jsonl.Parse("null");
            using var deep = Jsonl.Parse("{\"d\":" + JsonNested(100) + "}");
            Check(parsed?.RootElement.GetProperty("a").GetInt32() == 1 && literal?.RootElement.ValueKind == JsonValueKind.Null && deep is not null,
                "parses JSON text, the null literal and nesting beyond the default depth");
            Check(Jsonl.Parse(null) is null && Jsonl.Parse("") is null && Jsonl.Parse("{\"a\":") is null && Jsonl.Parse("{\"d\":" + JsonNested(600) + "}") is null,
                "returns null for missing, empty, unfinished or too deeply nested JSON text");
        }

        private static void JsonlCountChecks(string directory)
        {
            // BOM, CRLF, JSON null, an array, a blank line, an unfinished object, invalid UTF-8, nesting past 512, nesting past 64, a last line without newline.
            var path = Path.Combine(directory, "counts.jsonl");
            File.WriteAllBytes(path,
            [
                0xEF, 0xBB, 0xBF, .. "{\"a\":1}\n{\"a\":2}\r\nnull\n[1]\n\n{\"a\":\n{\"s\":\""u8, 0xFF, .. "\"}\n"u8,
                .. Encoding.UTF8.GetBytes("{\"a\":5,\"d\":" + JsonNested(600) + "}\n{\"a\":4,\"d\":" + JsonNested(100) + "}\n{\"a\":3}")
            ]);
            var read = new JsonlStats();
            var values = Jsonl.Read(path, read).Select(entry => ((JsonElement?)entry).Get("a").IntOr(0)).ToList();
            Check(values.SequenceEqual([1L, 2, 4, 3]) && read is { Malformed: 4, PartialTrailing: 0 }, "reads JSON Lines and counts malformed lines");
            var nodes = new JsonlStats();
            var nodeValues = Jsonl.ReadNodes(path, nodes).Select(node => node["a"].Str()).ToList();
            Check(nodeValues.SequenceEqual(["1", "2", "4", "3"]) && nodes is { Malformed: 4, PartialTrailing: 0 }, "reads JSON Lines nodes and counts malformed lines");

            var partial = JsonlWrite(directory, "partial.jsonl", "{\"a\":1}\n{\"a\":"u8);
            Check(JsonlRead(partial) == (1, 0, 1) && JsonlReadNodes(partial) == (1, 0, 1), "counts an unfinished last line as partial");
            var invalid = JsonlWrite(directory, "invalid.jsonl", [.. "{\"a\":1}\n{\"s\":\""u8, 0xFF, .. "\"}"u8]);
            Check(JsonlRead(invalid) == (1, 0, 1) && JsonlReadNodes(invalid) == (1, 0, 1), "counts a last line of invalid UTF-8 as partial");
            var carriage = JsonlWrite(directory, "carriage.jsonl", "{\"a\":1}\n{\"a\":\r"u8);
            Check(JsonlRead(carriage) == (1, 1, 0) && JsonlReadNodes(carriage) == (1, 1, 0), "counts a last line ending in CR as malformed");
            var empty = JsonlWrite(directory, "empty.jsonl", ""u8);
            Check(JsonlRead(empty) == (0, 0, 0) && JsonlReadNodes(empty) == (0, 0, 0), "reads an empty file");

            var missing = Path.Combine(directory, "missing.jsonl");
            var lazy = Jsonl.Read(missing, new JsonlStats());
            var lazyNodes = Jsonl.ReadNodes(missing, new JsonlStats());
            Check(JsonFailure(lazy.Any).StartsWith("FileNotFoundException: ", StringComparison.Ordinal)
                  && JsonFailure(lazyNodes.Any).StartsWith("FileNotFoundException: ", StringComparison.Ordinal), "opens JSON Lines files only when enumerated");
        }

        private static void JsonlFilterChecks(string directory)
        {
            var filtered = JsonlWrite(directory, "filtered.jsonl",
                "{\"type\":\"x\",\"a\":1}\n{\"type\":\"y\",\"a\":2}\n{\"type\":\"z\",\"a\":5}\n{\"type\":\"x\",\n{\"type\":\"y\",\n{\"type\":\"y\""u8);
            Check(JsonlRead(filtered, "\"type\":\"x\"", "\"type\":\"z\"") == (2, 1, 1), "parses only lines with a token but still counts an unfinished last line");
            Check(JsonlRead(filtered) == (3, 2, 1), "parses every line without tokens");
            var complete = JsonlWrite(directory, "complete.jsonl", "{\"type\":\"x\",\"a\":1}\n{\"type\":\"y\"}"u8);
            Check(JsonlRead(complete, "\"type\":\"x\"") == (1, 0, 0), "accepts an unmatched last line that parses");
        }

        private static void SqliteReaderChecks(string directory)
        {
            var missing = Path.Combine(directory, "missing.db");
            var opened = SqliteReader.Open(missing);
            Check(opened.IsFailure && opened.Error.StartsWith($"IOException: Cannot open {missing}: ", StringComparison.Ordinal) && !File.Exists(missing),
                "reports a missing database as a failure without creating it");
            var empty = Path.Combine(directory, "empty.db");
            File.WriteAllBytes(empty, []);
            var probe = Path.Combine(directory, "probe.db");
            using (var scratch = SqliteReader.Open(empty).Value)
            {
                Check(!scratch.HasTable("probe"), "finds no table in an empty database");
                // The reader is read-only, so build the table in an attached in-memory database and copy that database to a file.
                scratch.Execute("ATTACH 'file:scratch?mode=memory' AS scratch");
                scratch.Execute("CREATE TABLE scratch.probe (x)");
                scratch.Execute("INSERT INTO scratch.probe VALUES (1), ('a''b'), (NULL), (2.5)");
                scratch.Execute($"VACUUM scratch INTO '{probe.Replace("'", "''", StringComparison.Ordinal)}'");
            }
            using var database = new SqliteReader(probe);
            Check(database.HasTable("probe") && database.HasTable("PROBE") && !database.HasTable("missing") && !database.HasTable("it's"),
                "probes sqlite_master for a table");
            Check(database.TryQuery("SELECT x FROM probe ORDER BY rowid") is { IsSuccess: true, Value: [[1L], ["a'b"], [null], [2.5]] }, "materializes query rows");
            Check(database.TryQuery("SELECT x FROM missing") is { IsFailure: true, Error: "IOException: no such table: missing" }, "reports a failed query");
            var deferred = database.Query("SELECT x FROM missing");
            Check(JsonFailure(deferred.Any) == "IOException: no such table: missing", "prepares a query only when enumerated");
            Check(database.TryQuery("SELECT count(*) FROM probe") is { IsSuccess: true, Value: [[4L]] }, "keeps the connection usable after a failed query");
        }

        private static string JsonlWrite(string directory, string name, ReadOnlySpan<byte> content)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        private static (int Objects, long Malformed, long Partial) JsonlRead(string path, params string[] tokens)
        {
            var stats = new JsonlStats();
            var objects = Jsonl.Read(path, stats, tokens).Count();
            return (objects, stats.Malformed, stats.PartialTrailing);
        }

        private static (int Objects, long Malformed, long Partial) JsonlReadNodes(string path)
        {
            var stats = new JsonlStats();
            var objects = Jsonl.ReadNodes(path, stats).Count();
            return (objects, stats.Malformed, stats.PartialTrailing);
        }

        private static string JsonNested(int depth) => new string('[', depth) + new string(']', depth);

        // The failure text of an action that is expected to throw, in the persisted "{Type}: {Message}" format; empty when it succeeds.
        private static string JsonFailure<T>(Func<T> action)
        {
            try
            {
                _ = action();
                return "";
            }
            catch (Exception error) when (error is FormatException or OverflowException or IOException)
            {
                return Attempt.Describe(error);
            }
        }
    }
}
