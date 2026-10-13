using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Diyalog360.Api.Services;

public record QueryColumn(string Name, string Kind); // number | date | text

public record QueryResult(
    IReadOnlyList<QueryColumn> Columns, List<object?[]> Rows, bool Truncated, long DurationMs,
    IReadOnlyList<string> Plan, IReadOnlyList<string> PlanWarnings, IReadOnlyList<string> Tables, IReadOnlyList<string> UsedColumns);

public record QueryOutcome(GuardStatus Status, string? Error, QueryResult? Result);

/// <summary>Doğrulanmış SQL'i read-only bağlantıda, zaman aşımı ve satır limitiyle çalıştırır.</summary>
public sealed class QueryRunner(string bankDbPath, SqlGuard guard, SchemaCatalog catalog, int maxRows = 1000, int timeoutSeconds = 15)
{
    const long LargeTable = 50_000;
    static readonly Regex DateValue = new(@"^\d{4}-\d{2}(-\d{2})?([ T]\d{2}:\d{2}(:\d{2})?)?$", RegexOptions.Compiled);

    public QueryOutcome Run(string sql)
    {
        sql = sql.Trim().TrimEnd(';').Trim();
        using var conn = new SqliteConnection($"Data Source={bankDbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        // printf('%900000000c','x') gibi tek bir SELECT'in GB'larca bellek ayırmasını engelle
        raw.sqlite3_limit(conn.Handle, raw.SQLITE_LIMIT_LENGTH, 1_000_000);
        raw.sqlite3_limit(conn.Handle, raw.SQLITE_LIMIT_SQL_LENGTH, 20_000);

        var check = guard.Validate(conn, sql);
        if (check.Status != GuardStatus.Ok) return new(check.Status, check.Error, null);

        var sw = Stopwatch.StartNew();
        delegate_progress onProgress = _ => sw.Elapsed.TotalSeconds > timeoutSeconds ? 1 : 0;
        raw.sqlite3_progress_handler(conn.Handle, 10_000, onProgress, null);

        try
        {
            var (plan, warnings) = ExplainPlan(conn, sql);
            sw.Restart();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            var rows = new List<object?[]>();
            bool truncated = false;
            while (reader.Read())
            {
                if (rows.Count == maxRows) { truncated = true; break; }
                var row = new object?[reader.FieldCount];
                for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }

            var columns = Enumerable.Range(0, reader.FieldCount)
                .Select(i => new QueryColumn(reader.GetName(i), InferKind(rows, i))).ToList();
            GC.KeepAlive(onProgress);
            GC.KeepAlive(check);
            return new(GuardStatus.Ok, null, new QueryResult(columns, rows, truncated, sw.ElapsedMilliseconds, plan, warnings, check.Tables, check.Columns));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == raw.SQLITE_INTERRUPT)
        {
            return new(GuardStatus.Invalid, $"Sorgu {timeoutSeconds} saniyede bitmedi ve durduruldu. Daha basit veya daha çok filtrelenmiş bir sorgu yaz.", null);
        }
        catch (SqliteException ex)
        {
            return new(GuardStatus.Invalid, "SQL çalıştırma hatası: " + ex.Message, null);
        }
    }

    (List<string> Plan, List<string> Warnings) ExplainPlan(SqliteConnection conn, string sql)
    {
        var plan = new List<string>();
        var warnings = new List<string>();
        // Plan satırları tablo yerine alias gösterebilir ("SCAN i"); alias → tablo eşlemesi
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match a in Regex.Matches(sql, @"\b(?:FROM|JOIN)\s+""?(\w+)""?(?:\s+(?:AS\s+)?(\w+))?", RegexOptions.IgnoreCase))
        {
            aliases[a.Groups[1].Value] = a.Groups[1].Value;
            if (a.Groups[2].Success) aliases.TryAdd(a.Groups[2].Value, a.Groups[1].Value);
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var detail = reader.GetString(3);
            plan.Add(detail);
            // "SCAN Islem" = tam tablo taraması; "SEARCH ... USING INDEX" = index kullanımı
            var m = Regex.Match(detail, @"^SCAN (\w+)(?: AS \w+)?$");
            if (m.Success)
            {
                var name = aliases.GetValueOrDefault(m.Groups[1].Value, m.Groups[1].Value);
                var table = catalog.Tables.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (table is { RowCount: > LargeTable })
                    warnings.Add($"{table.Name} tablosu index kullanılmadan baştan sona taranıyor (~{table.RowCount.ToString("N0", CultureInfo.GetCultureInfo("tr-TR"))} satır).");
            }
        }
        return (plan, warnings);
    }

    static string InferKind(List<object?[]> rows, int col)
    {
        var values = rows.Select(r => r[col]).Where(v => v != null).Take(200).ToList();
        if (values.Count == 0) return "text";
        if (values.All(v => v is long or double)) return "number";
        if (values.All(v => v is string s && DateValue.IsMatch(s))) return "date";
        return "text";
    }
}
