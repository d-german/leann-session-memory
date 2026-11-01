using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CSharpFunctionalExtensions;

namespace SessionMemory
{
    /// <summary>Queries through the operating system SQLite engine, read-only unless explicitly created writable.</summary>
    sealed internal partial class SqliteReader : IDisposable
    {
        private const string Library = "winsqlite3.dll";
        private const int Ok = 0;
        private const int Row = 100;
        private const int Done = 101;
        private const int OpenReadOnly = 0x1;
        private const int OpenReadWrite = 0x2;
        private const int OpenCreate = 0x4;
        private const int OpenUri = 0x40;
        private const int IntegerColumn = 1;
        private const int FloatColumn = 2;
        private const int NullColumn = 5;

        private IntPtr _handle;

        static SqliteReader() => NativeLibrary.SetDllImportResolver(typeof(SqliteReader).Assembly, Resolve);

        public SqliteReader(string path) : this(path, false)
        {
        }

        private SqliteReader(string path, bool writable)
        {
            var name = writable && path == ":memory:" ? path : new Uri(path).AbsoluteUri + (writable ? "?mode=rwc" : "?mode=ro");
            var uri = Encoding.UTF8.GetBytes(name + "\0");
            var flags = writable ? OpenReadWrite | OpenCreate | OpenUri : OpenReadOnly | OpenUri;
            if (sqlite3_open_v2(uri, out _handle, flags, IntPtr.Zero) == Ok && sqlite3_busy_timeout(_handle, 5000) == Ok) return;
            var message = ErrorMessage();
            Dispose();
            throw new IOException($"Cannot open {path}: {message}");
        }

        // A database that cannot be opened is a failure carrying the constructor's "IOException: Cannot open ..." text.
        public static Result<SqliteReader, string> Open(string path) => Attempt.Io(() => new SqliteReader(path));

        public static SqliteReader Create(string path) => new(path, true);

        // Windows keeps the default lookup of winsqlite3.dll; elsewhere the same imports bind to the system SQLite.
        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
            name != Library || OperatingSystem.IsWindows()
                ? IntPtr.Zero
                : NativeLibrary.Load(OperatingSystem.IsMacOS() ? "libsqlite3.dylib" : "libsqlite3.so.0", assembly, searchPath);

        public IEnumerable<object?[]> Query(string sql, params object?[] parameters)
        {
            var statement = IntPtr.Zero;
            try
            {
                if (sqlite3_prepare16_v2(_handle, sql, -1, out statement, IntPtr.Zero) != Ok)
                    throw new IOException(parameters.Length == 0 ? ErrorMessage() : "SQLite statement preparation failed.");
                if (sqlite3_bind_parameter_count(statement) != parameters.Length)
                    throw new IOException("SQLite parameter count does not match the statement.");
                for (var i = 0; i < parameters.Length; i++)
                {
                    var status = parameters[i] switch
                    {
                        null => sqlite3_bind_null(statement, i + 1),
                        string value => sqlite3_bind_text16(statement, i + 1, value, checked(value.Length * 2), new IntPtr(-1)),
                        bool value => sqlite3_bind_int64(statement, i + 1, value ? 1 : 0),
                        int value => sqlite3_bind_int64(statement, i + 1, value),
                        long value => sqlite3_bind_int64(statement, i + 1, value),
                        double value => sqlite3_bind_double(statement, i + 1, value),
                        _ => throw new IOException("Unsupported SQLite parameter type.")
                    };
                    if (status != Ok) throw new IOException("SQLite parameter binding failed.");
                }
                while (true)
                {
                    var step = sqlite3_step(statement);
                    if (step == Done) yield break;
                    if (step != Row) throw new IOException(parameters.Length == 0 ? ErrorMessage() : "SQLite statement execution failed.");
                    var row = new object?[sqlite3_column_count(statement)];
                    for (var column = 0; column < row.Length; column++)
                    {
                        row[column] = sqlite3_column_type(statement, column) switch
                        {
                            IntegerColumn => sqlite3_column_int64(statement, column),
                            FloatColumn => sqlite3_column_double(statement, column),
                            NullColumn => null,
                            _ => Marshal.PtrToStringUni(sqlite3_column_text16(statement, column), sqlite3_column_bytes16(statement, column) / 2)
                        };
                    }
                    yield return row;
                }
            }
            finally
            {
                // Finalize fails only by repeating the error of the last step, which is already being thrown.
                if (statement != IntPtr.Zero) _ = sqlite3_finalize(statement);
            }
        }

        // Materializes every row (the statement is finalized before returning), so use it for small results only.
        public Result<ImmutableArray<object?[]>, string> TryQuery(string sql, params object?[] parameters) => Attempt.Io(() => Query(sql, parameters).ToImmutableArray());

        // Probes the main schema the way SQLite resolves table names (ASCII case-insensitive); real I/O errors still throw.
        public bool HasTable(string name) =>
            Query($"SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '{name.Replace("'", "''", StringComparison.Ordinal)}' COLLATE NOCASE").Any();

        public void Execute(string sql, params object?[] parameters)
        {
            foreach (var _ in Query(sql, parameters))
            {
            }
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            // close_v2 fails only for an invalid handle, and Dispose must not throw.
            _ = sqlite3_close_v2(_handle);
            _handle = IntPtr.Zero;
        }

        private string ErrorMessage() =>
            _handle == IntPtr.Zero ? "unknown SQLite error" : Marshal.PtrToStringUni(sqlite3_errmsg16(_handle)) ?? "unknown SQLite error";

        [LibraryImport(Library)]
        private static partial int sqlite3_open_v2([In] byte[] filename, out IntPtr database, int flags, IntPtr vfs);

        [LibraryImport(Library)]
        private static partial int sqlite3_close_v2(IntPtr database);

        [LibraryImport(Library)]
        private static partial int sqlite3_busy_timeout(IntPtr database, int milliseconds);

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
        private static partial int sqlite3_prepare16_v2(IntPtr database, string sql, int bytes, out IntPtr statement, IntPtr tail);

        [LibraryImport(Library)]
        private static partial int sqlite3_bind_parameter_count(IntPtr statement);

        [LibraryImport(Library)]
        private static partial int sqlite3_bind_null(IntPtr statement, int index);

        [LibraryImport(Library)]
        private static partial int sqlite3_bind_int64(IntPtr statement, int index, long value);

        [LibraryImport(Library)]
        private static partial int sqlite3_bind_double(IntPtr statement, int index, double value);

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
        private static partial int sqlite3_bind_text16(IntPtr statement, int index, string value, int bytes, IntPtr destructor);

        [LibraryImport(Library)]
        private static partial int sqlite3_step(IntPtr statement);

        [LibraryImport(Library)]
        private static partial int sqlite3_finalize(IntPtr statement);

        [LibraryImport(Library)]
        private static partial int sqlite3_column_count(IntPtr statement);

        [LibraryImport(Library)]
        private static partial int sqlite3_column_type(IntPtr statement, int column);

        [LibraryImport(Library)]
        private static partial long sqlite3_column_int64(IntPtr statement, int column);

        [LibraryImport(Library)]
        private static partial double sqlite3_column_double(IntPtr statement, int column);

        [LibraryImport(Library)]
        private static partial IntPtr sqlite3_column_text16(IntPtr statement, int column);

        [LibraryImport(Library)]
        private static partial int sqlite3_column_bytes16(IntPtr statement, int column);

        [LibraryImport(Library)]
        private static partial IntPtr sqlite3_errmsg16(IntPtr database);
    }
}
