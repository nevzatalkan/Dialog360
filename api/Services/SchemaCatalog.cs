using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Diyalog360.Api.Services;

public record ColumnInfo(string Name, string Type, bool IsPk, string? FkTable, string? FkColumn,
    string? Description, bool IsPii, IReadOnlyList<string>? Values);

public record TableInfo(string Name, string? Description, long RowCount, IReadOnlyList<ColumnInfo> Columns);

/// <summary>
/// Şemayı veritabanından okur (tablo, kolon, tip, PK/FK, düşük kardinaliteli değerler) ve
/// elle yazılmış açıklamalarla (Prompt/schema.notes.json) birleştirip LLM'e verilecek kompakt metni üretir.
/// </summary>
public sealed class SchemaCatalog
{
    const int MaxDistinctValues = 12;
    static readonly Regex DateLike = new(@"^\d{4}-\d{2}-\d{2}", RegexOptions.Compiled);

    public IReadOnlyList<TableInfo> Tables { get; }
    public string PromptText { get; }
    public HashSet<string> AllowedTables { get; }
    /// <summary>"Tablo.Kolon" biçiminde, büyük/küçük harf duyarsız.</summary>
    public HashSet<string> PiiColumns { get; }

    public SchemaCatalog(string bankDbPath, string notesPath)
    {
        var notes = File.Exists(notesPath) ? Notes.Load(notesPath) : new Notes();
        using var conn = new SqliteConnection($"Data Source={bankDbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();

        var tableNames = Query(conn, "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY rowid")
            .Select(r => (string)r[0]!).ToList();

        var tables = new List<TableInfo>();
        foreach (var table in tableNames)
        {
            var fks = Query(conn, $"SELECT \"from\", \"table\", \"to\" FROM pragma_foreign_key_list('{table}')")
                .ToDictionary(r => (string)r[0]!, r => ((string)r[1]!, (string?)r[2] ?? "Id"), StringComparer.OrdinalIgnoreCase);
            long rowCount = (long)Query(conn, $"SELECT COUNT(*) FROM \"{table}\"")[0][0]!;

            var columns = new List<ColumnInfo>();
            foreach (var c in Query(conn, $"SELECT name, type, pk FROM pragma_table_info('{table}')"))
            {
                string name = (string)c[0]!, type = (string)c[1]!;
                bool isPk = (long)c[2]! > 0;
                var (desc, pii) = notes.Column(table, name);
                fks.TryGetValue(name, out var fk);
                IReadOnlyList<string>? values = null;
                if (!pii && !isPk && fk == default && type.Equals("TEXT", StringComparison.OrdinalIgnoreCase))
                    values = DistinctValues(conn, table, name);
                columns.Add(new ColumnInfo(name, type, isPk, fk == default ? null : fk.Item1, fk == default ? null : fk.Item2, desc, pii, values));
            }
            tables.Add(new TableInfo(table, notes.Table(table), rowCount, columns));
        }

        Tables = tables;
        AllowedTables = new HashSet<string>(tableNames, StringComparer.OrdinalIgnoreCase);
        PiiColumns = new HashSet<string>(
            tables.SelectMany(t => t.Columns.Where(c => c.IsPii).Select(c => $"{t.Name}.{c.Name}")), StringComparer.OrdinalIgnoreCase);
        PromptText = BuildPromptText(tables);
    }

    static IReadOnlyList<string>? DistinctValues(SqliteConnection conn, string table, string column)
    {
        var rows = Query(conn, $"SELECT DISTINCT \"{column}\" FROM \"{table}\" WHERE \"{column}\" IS NOT NULL LIMIT {MaxDistinctValues + 1}");
        if (rows.Count > MaxDistinctValues) return null;
        var values = rows.Select(r => Convert.ToString(r[0], CultureInfo.InvariantCulture)!).ToList();
        return values.Any(v => DateLike.IsMatch(v)) ? null : values.Order(StringComparer.Ordinal).ToList();
    }

    static string BuildPromptText(List<TableInfo> tables)
    {
        var sb = new StringBuilder();
        var tr = CultureInfo.GetCultureInfo("tr-TR");
        foreach (var t in tables)
        {
            sb.Append($"### {t.Name}");
            if (t.Description != null) sb.Append($" — {t.Description}");
            sb.AppendLine($" ({t.RowCount.ToString("N0", tr)} satır)");
            foreach (var c in t.Columns)
            {
                sb.Append($"- {c.Name} {c.Type}");
                if (c.IsPk) sb.Append(" PK");
                if (c.FkTable != null) sb.Append($" FK→{c.FkTable}.{c.FkColumn}");
                if (c.IsPii) sb.Append(" [KİŞİSEL VERİ — SORGUDA KULLANMA]");
                if (c.Description != null) sb.Append($" — {c.Description}");
                if (c.Values != null) sb.Append($" · değerler: {string.Join(", ", c.Values.Select(v => $"'{v}'"))}");
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        sb.AppendLine("### İlişkiler (JOIN koşulları)");
        foreach (var t in tables)
            foreach (var c in t.Columns.Where(c => c.FkTable != null))
                sb.AppendLine($"- {t.Name}.{c.Name} = {c.FkTable}.{c.FkColumn}");
        return sb.ToString();
    }

    static List<object?[]> Query(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>schema.notes.json: kolon değeri ya düz açıklama metni ya da { description, pii } nesnesidir.</summary>
    sealed class Notes
    {
        readonly Dictionary<string, JsonElement> _tables = new(StringComparer.OrdinalIgnoreCase);

        public static Notes Load(string path)
        {
            var notes = new Notes();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var t in doc.RootElement.GetProperty("tables").EnumerateObject())
                notes._tables[t.Name] = t.Value.Clone();
            return notes;
        }

        public string? Table(string table) =>
            _tables.TryGetValue(table, out var t) && t.TryGetProperty("description", out var d) ? d.GetString() : null;

        public (string? Description, bool Pii) Column(string table, string column)
        {
            if (!_tables.TryGetValue(table, out var t) || !t.TryGetProperty("columns", out var cols)) return (null, false);
            var match = cols.EnumerateObject().FirstOrDefault(p => p.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
            if (match.Value.ValueKind == JsonValueKind.String) return (match.Value.GetString(), false);
            if (match.Value.ValueKind == JsonValueKind.Object)
                return (match.Value.TryGetProperty("description", out var d) ? d.GetString() : null,
                        match.Value.TryGetProperty("pii", out var p) && p.GetBoolean());
            return (null, false);
        }
    }
}
