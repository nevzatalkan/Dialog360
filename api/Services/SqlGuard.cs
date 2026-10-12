using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Diyalog360.Api.Services;

public enum GuardStatus { Ok, Invalid, Forbidden }

/// <param name="Status">Ok: çalıştırılabilir. Invalid: LLM'e geri verilip düzeltilebilir. Forbidden: güvenlik ihlali, düzeltme denenmez.</param>
public sealed record GuardResult(GuardStatus Status, string? Error, IReadOnlyList<string> Tables, IReadOnlyList<string> Columns)
{
    internal object? KeepAlive { get; init; }
}

/// <summary>
/// LLM'in ürettiği SQL'i SQLite'ın kendi parser'ı ile denetler.
/// SQLite sorguyu derlerken ayrıştırma ağacındaki her erişim için (tablo/kolon okuma, fonksiyon çağrısı,
/// yazma, PRAGMA, ATTACH...) authorizer'ı çağırır; burada yalnızca okumaya izin verilir.
/// Authorizer bağlantıda kalır, yani sorgu çalıştırılırken de aynı kurallar geçerlidir.
///
/// Katmanlar: (1) bağlantı Mode=ReadOnly, (2) bu authorizer + tek ifade + sqlite3_stmt_readonly,
/// (3) çalıştırmada zaman aşımı ve satır limiti (QueryRunner).
/// </summary>
public sealed class SqlGuard(SchemaCatalog catalog)
{
    // https://www.sqlite.org/c3ref/c_alter_table.html
    const int SQLITE_READ = 20, SQLITE_SELECT = 21, SQLITE_FUNCTION = 31, SQLITE_RECURSIVE = 33;
    const int SQLITE_OK = 0, SQLITE_DENY = 1;

    static readonly HashSet<string> AllowedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "count", "sum", "total", "avg", "min", "max", "group_concat", "string_agg",
        "date", "time", "datetime", "julianday", "strftime", "unixepoch", "timediff",
        "abs", "round", "ceil", "ceiling", "floor", "power", "pow", "sqrt", "mod", "sign", "ln", "log", "log10", "exp",
        "lower", "upper", "length", "substr", "substring", "trim", "ltrim", "rtrim", "replace", "instr",
        "printf", "format", "concat", "concat_ws", "like", "glob", "char", "unicode",
        "coalesce", "ifnull", "nullif", "iif", "typeof", "likely", "unlikely",
        "row_number", "rank", "dense_rank", "percent_rank", "cume_dist", "ntile",
        "lag", "lead", "first_value", "last_value", "nth_value",
    };

    static readonly Dictionary<int, string> ForbiddenActions = new()
    {
        [1] = "CREATE INDEX", [2] = "CREATE TABLE", [3] = "CREATE TEMP INDEX", [4] = "CREATE TEMP TABLE",
        [5] = "CREATE TEMP TRIGGER", [6] = "CREATE TEMP VIEW", [7] = "CREATE TRIGGER", [8] = "CREATE VIEW",
        [9] = "DELETE", [10] = "DROP INDEX", [11] = "DROP TABLE", [12] = "DROP TEMP INDEX", [13] = "DROP TEMP TABLE",
        [14] = "DROP TEMP TRIGGER", [15] = "DROP TEMP VIEW", [16] = "DROP TRIGGER", [17] = "DROP VIEW",
        [18] = "INSERT", [19] = "PRAGMA", [22] = "TRANSACTION", [23] = "UPDATE", [24] = "ATTACH", [25] = "DETACH",
        [26] = "ALTER TABLE", [27] = "REINDEX", [28] = "ANALYZE", [29] = "CREATE VIRTUAL TABLE",
        [30] = "DROP VIRTUAL TABLE", [32] = "SAVEPOINT",
    };

    sealed class Context
    {
        public readonly List<string> Forbidden = [];
        public readonly List<string> Invalid = [];
        public readonly SortedSet<string> Tables = new(StringComparer.OrdinalIgnoreCase);
        public readonly SortedSet<string> Columns = new(StringComparer.OrdinalIgnoreCase);
        public strdelegate_authorizer? Callback;
    }

    public GuardResult Validate(SqliteConnection conn, string sql)
    {
        var ctx = new Context();
        ctx.Callback = (_, action, p0, p1, db, _) => Authorize(ctx, action, p0, p1, db);
        raw.sqlite3_set_authorizer(conn.Handle, ctx.Callback, null);

        GuardResult Result(GuardStatus status, string? error) =>
            new(status, error, ctx.Tables.ToList(), ctx.Columns.ToList()) { KeepAlive = ctx };

        int rc = raw.sqlite3_prepare_v2(conn.Handle, sql, out sqlite3_stmt stmt, out string tail);
        try
        {
            if (ctx.Forbidden.Count > 0) return Result(GuardStatus.Forbidden, Forbidden(ctx));
            if (ctx.Invalid.Count > 0) return Result(GuardStatus.Invalid, string.Join(" ", ctx.Invalid.Distinct()));
            if (rc != raw.SQLITE_OK) return Result(GuardStatus.Invalid, "SQL hatası: " + raw.sqlite3_errmsg(conn.Handle).utf8_to_string());
            if (stmt is null || stmt.IsInvalid) return Result(GuardStatus.Invalid, "Boş sorgu: tek bir SELECT ifadesi yaz.");

            if (!string.IsNullOrWhiteSpace(tail))
            {
                raw.sqlite3_prepare_v2(conn.Handle, tail, out sqlite3_stmt next, out _);
                bool hasSecond = next is { IsInvalid: false };
                next?.Dispose();
                if (ctx.Forbidden.Count > 0) return Result(GuardStatus.Forbidden, Forbidden(ctx));
                if (hasSecond) return Result(GuardStatus.Invalid, "Yalnızca tek bir SQL ifadesine izin verilir; noktalı virgülle ayrılmış ikinci ifadeyi kaldır.");
            }

            if (raw.sqlite3_stmt_readonly(stmt) == 0)
                return Result(GuardStatus.Forbidden, "Sorgu veritabanında değişiklik yapıyor; yalnızca okuma (SELECT) sorgularına izin verilir.");

            return Result(GuardStatus.Ok, null);
        }
        finally
        {
            stmt?.Dispose();
        }
    }

    int Authorize(Context ctx, int action, string? p0, string? p1, string? db)
    {
        switch (action)
        {
            case SQLITE_SELECT:
            case SQLITE_RECURSIVE:
                return SQLITE_OK;

            case SQLITE_READ:
                if (db != null && db != "main")
                    return Deny(ctx.Forbidden, $"'{db}' veritabanına erişim yasak.");
                if (p0 == null || !catalog.AllowedTables.Contains(p0))
                    return Deny(ctx.Invalid, $"'{p0}' tablosu kullanılamaz; yalnızca şemadaki tabloları kullan.");
                ctx.Tables.Add(p0);
                if (!string.IsNullOrEmpty(p1))
                {
                    if (catalog.PiiColumns.Contains($"{p0}.{p1}"))
                        return Deny(ctx.Invalid, $"{p0}.{p1} kişisel veri (KVKK) içerdiği için sorguda kullanılamaz; bu kolonu sorgudan tamamen çıkar (müşteriyi göstermek için Musteri.Id kullan).");
                    ctx.Columns.Add($"{p0}.{p1}");
                }
                return SQLITE_OK;

            case SQLITE_FUNCTION:
                return p1 != null && AllowedFunctions.Contains(p1)
                    ? SQLITE_OK
                    : Deny(ctx.Invalid, $"'{p1}' fonksiyonu kullanılamaz.");

            default:
                return Deny(ctx.Forbidden, ForbiddenActions.GetValueOrDefault(action, $"işlem #{action}"));
        }
    }

    static int Deny(List<string> list, string message)
    {
        list.Add(message);
        return SQLITE_DENY;
    }

    static string Forbidden(Context ctx) =>
        $"Yalnızca okuma sorgularına izin verilir. Engellenen işlem: {string.Join(", ", ctx.Forbidden.Distinct())}.";
}
