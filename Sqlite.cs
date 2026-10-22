using System.Runtime.InteropServices;
using System.Text;

namespace SessionMemory;

/// <summary>Read-only queries through the SQLite engine that ships with Windows (winsqlite3.dll).</summary>
sealed class SqliteReader : IDisposable
{
    const string Library = "winsqlite3.dll";
    const int Ok = 0;
    const int Row = 100;
    const int Done = 101;
    const int OpenReadOnly = 0x1;
    const int OpenUri = 0x40;
    const int IntegerColumn = 1;
    const int FloatColumn = 2;
    const int NullColumn = 5;

    IntPtr handle;

    public SqliteReader(string path)
    {
        var uri = Encoding.UTF8.GetBytes(new Uri(path).AbsoluteUri + "?mode=ro\0");
        if (sqlite3_open_v2(uri, out handle, OpenReadOnly | OpenUri, IntPtr.Zero) != Ok)
        {
            var message = ErrorMessage();
            Dispose();
            throw new IOException($"Cannot open {path}: {message}");
        }
        sqlite3_busy_timeout(handle, 5000);
    }

    public IEnumerable<object?[]> Query(string sql)
    {
        if (sqlite3_prepare16_v2(handle, sql, -1, out var statement, IntPtr.Zero) != Ok)
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
                        _ => Marshal.PtrToStringUni(sqlite3_column_text16(statement, column), sqlite3_column_bytes16(statement, column) / 2),
                    };
                }
                yield return row;
            }
        }
        finally
        {
            sqlite3_finalize(statement);
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
        if (handle == IntPtr.Zero) return;
        sqlite3_close_v2(handle);
        handle = IntPtr.Zero;
    }

    string ErrorMessage() =>
        handle == IntPtr.Zero ? "unknown SQLite error" : Marshal.PtrToStringUni(sqlite3_errmsg16(handle)) ?? "unknown SQLite error";

    [DllImport(Library)]
    static extern int sqlite3_open_v2(byte[] filename, out IntPtr database, int flags, IntPtr vfs);

    [DllImport(Library)]
    static extern int sqlite3_close_v2(IntPtr database);

    [DllImport(Library)]
    static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

    [DllImport(Library)]
    static extern int sqlite3_prepare16_v2(IntPtr database, [MarshalAs(UnmanagedType.LPWStr)] string sql, int bytes, out IntPtr statement, IntPtr tail);

    [DllImport(Library)]
    static extern int sqlite3_step(IntPtr statement);

    [DllImport(Library)]
    static extern int sqlite3_finalize(IntPtr statement);

    [DllImport(Library)]
    static extern int sqlite3_column_count(IntPtr statement);

    [DllImport(Library)]
    static extern int sqlite3_column_type(IntPtr statement, int column);

    [DllImport(Library)]
    static extern long sqlite3_column_int64(IntPtr statement, int column);

    [DllImport(Library)]
    static extern double sqlite3_column_double(IntPtr statement, int column);

    [DllImport(Library)]
    static extern IntPtr sqlite3_column_text16(IntPtr statement, int column);

    [DllImport(Library)]
    static extern int sqlite3_column_bytes16(IntPtr statement, int column);

    [DllImport(Library)]
    static extern IntPtr sqlite3_errmsg16(IntPtr database);
}
