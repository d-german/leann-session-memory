using System.Runtime.InteropServices;
using System.Text;

namespace SessionMemory
{
    /// <summary>Read-only queries through the SQLite engine that ships with Windows (winsqlite3.dll).</summary>
    sealed internal partial class SqliteReader : IDisposable
    {
        private const string Library = "winsqlite3.dll";
        private const int Ok = 0;
        private const int Row = 100;
        private const int Done = 101;
        private const int OpenReadOnly = 0x1;
        private const int OpenUri = 0x40;
        private const int IntegerColumn = 1;
        private const int FloatColumn = 2;
        private const int NullColumn = 5;

        private IntPtr _handle;

        public SqliteReader(string path)
        {
            var uri = Encoding.UTF8.GetBytes(new Uri(path).AbsoluteUri + "?mode=ro\0");
            if (sqlite3_open_v2(uri, out _handle, OpenReadOnly | OpenUri, IntPtr.Zero) == Ok && sqlite3_busy_timeout(_handle, 5000) == Ok) return;
            var message = ErrorMessage();
            Dispose();
            throw new IOException($"Cannot open {path}: {message}");
        }

        public IEnumerable<object?[]> Query(string sql)
        {
            if (sqlite3_prepare16_v2(_handle, sql, -1, out var statement, IntPtr.Zero) != Ok)
                throw new IOException(ErrorMessage());
            try
            {
                while (true)
                {
                    var step = sqlite3_step(statement);
                    if (step == Done) yield break;
                    if (step != Row) throw new IOException(ErrorMessage());
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
                _ = sqlite3_finalize(statement);
            }
        }

        public void Execute(string sql)
        {
            foreach (var _ in Query(sql))
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
