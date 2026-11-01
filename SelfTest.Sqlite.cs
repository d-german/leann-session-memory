namespace SessionMemory
{
    internal static partial class SelfTest
    {
        private static void SqliteWriteChecks()
        {
            var temporary = Directory.CreateTempSubdirectory("session-memory-sqlite-write-");
            using var cleanup = new TemporaryDirectory(temporary.FullName);
            var path = Path.Combine(temporary.FullName, "test.db");
            const string special = "O'Brien' OR 1=1 --";
            const string unicode = "caf\u00e9 \u03a9 \U0001F600\0tail";
            using (var writer = SqliteReader.Create(path))
            {
                writer.Execute("CREATE TABLE values_test (value TEXT, number INTEGER, weight REAL, flag INTEGER, missing TEXT)");
                writer.Execute("INSERT INTO values_test VALUES (?, ?, ?, ?, ?)", special, 42L, 1.25, true, null);
                writer.Execute("INSERT INTO values_test VALUES (?, ?, ?, ?, ?)", unicode, -7, 0.5, false, null);
                var row = writer.Query("SELECT value, number, weight, flag, missing FROM values_test WHERE value = ?", special).Single();
                Check(row is [special, 42L, 1.25, 1L, null], "binds data containing apostrophes and SQL operators");
                Check(writer.Query("SELECT value FROM values_test WHERE value = ?", unicode).Single()[0] is string value && value == unicode,
                    "round-trips Unicode and embedded NUL text");
                Check(writer.Query("SELECT value FROM values_test WHERE value = ?", "' OR 1=1 --").Any() == false,
                    "does not interpret bound values as SQL");
                var invalid = writer.TryQuery("SELECT ?", new object());
                Check(invalid.IsFailure && !invalid.Error.Contains(special, StringComparison.Ordinal)
                                        && writer.Query("SELECT count(*) FROM values_test").Single()[0] is 2L,
                    "finalizes a failed binding and keeps the connection usable");
                writer.Execute("BEGIN");
                writer.Execute("INSERT INTO values_test (value) VALUES (?)", "rolled back");
                writer.Execute("ROLLBACK");
            }
            using (var reader = new SqliteReader(path))
            {
                Check(reader.Query("SELECT count(*) FROM values_test WHERE value = ?", "rolled back").Single()[0] is 0L,
                    "does not publish a rolled-back transaction");
                var rejected = reader.TryQuery("INSERT INTO values_test (value) VALUES (?)", "read-only test");
                Check(rejected.IsFailure && reader.Query("SELECT count(*) FROM values_test").Single()[0] is 2L,
                    "the default constructor remains read-only");
            }

            using var memory = SqliteReader.Create(":memory:");
            memory.Execute("CREATE VIRTUAL TABLE docs USING fts5(body, tokenize = 'porter unicode61')");
            memory.Execute("INSERT INTO docs (body) VALUES (?)", "The runner was running on a trail.");
            memory.Execute("INSERT INTO docs (body) VALUES (?)", "A boat crossed the water.");
            var hits = memory.Query("SELECT body, bm25(docs) FROM docs WHERE docs MATCH ? ORDER BY bm25(docs)", "run").ToArray();
            Check(hits is [[string text, double]] && text.Contains("running", StringComparison.Ordinal),
                "supports in-memory FTS5 porter stemming and BM25");
        }
    }
}
