using Microsoft.Data.Sqlite;

namespace Diyalog360.Api.Services;

public record StoredMessage(long Id, string Role, string Content, string CreatedAt);

/// <summary>Uygulama verisi (app.db): sohbet geçmişi, LLM cevap önbelleği ve denetim logu.</summary>
public sealed class AppStore
{
    readonly string _cs;

    public AppStore(string path)
    {
        _cs = $"Data Source={path}";
        Exec("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS Chat (Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')));
            CREATE TABLE IF NOT EXISTS Message (Id INTEGER PRIMARY KEY, ChatId TEXT NOT NULL REFERENCES Chat(Id),
                Role TEXT NOT NULL, Content TEXT NOT NULL, CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')));
            CREATE INDEX IF NOT EXISTS IX_Message_ChatId ON Message(ChatId);
            CREATE TABLE IF NOT EXISTS LlmCache (Key TEXT PRIMARY KEY, Question TEXT NOT NULL, Answer TEXT NOT NULL,
                Hits INTEGER NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')));
            CREATE TABLE IF NOT EXISTS QueryLog (Id INTEGER PRIMARY KEY, ChatId TEXT, Question TEXT, Kind TEXT, Sql TEXT,
                Rows INTEGER, Attempts INTEGER, FromCache INTEGER, LlmMs INTEGER, DbMs INTEGER, TotalMs INTEGER, Error TEXT,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')));
            """);
    }

    public string EnsureChat(string? chatId)
    {
        chatId = string.IsNullOrWhiteSpace(chatId) ? Guid.NewGuid().ToString("N")[..12] : chatId;
        Exec("INSERT OR IGNORE INTO Chat (Id) VALUES ($a)", chatId);
        return chatId;
    }

    public long AddMessage(string chatId, string role, string content) =>
        (long)Scalar("INSERT INTO Message (ChatId, Role, Content) VALUES ($a, $b, $c) RETURNING Id", chatId, role, content)!;

    public List<StoredMessage> GetMessages(string chatId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Role, Content, CreatedAt FROM Message WHERE ChatId = $a ORDER BY Id";
        cmd.Parameters.AddWithValue("$a", chatId);
        using var r = cmd.ExecuteReader();
        var list = new List<StoredMessage>();
        while (r.Read()) list.Add(new StoredMessage(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    public string? CacheGet(string key)
    {
        var answer = (string?)Scalar("SELECT Answer FROM LlmCache WHERE Key = $a", key);
        if (answer != null) Exec("UPDATE LlmCache SET Hits = Hits + 1 WHERE Key = $a", key);
        return answer;
    }

    public void CachePut(string key, string question, string answer) =>
        Exec("INSERT OR REPLACE INTO LlmCache (Key, Question, Answer) VALUES ($a, $b, $c)", key, question, answer);

    public void CacheRemove(string key) => Exec("DELETE FROM LlmCache WHERE Key = $a", key);

    public void Log(string chatId, string question, string kind, string? sql, int rows, int attempts, bool fromCache,
        long llmMs, long dbMs, long totalMs, string? error) =>
        Exec("INSERT INTO QueryLog (ChatId, Question, Kind, Sql, Rows, Attempts, FromCache, LlmMs, DbMs, TotalMs, Error) VALUES ($a,$b,$c,$d,$e,$f,$g,$h,$i,$j,$k)",
            chatId, question, kind, sql, rows, attempts, fromCache ? 1 : 0, llmMs, dbMs, totalMs, error);

    SqliteConnection Open()
    {
        var conn = new SqliteConnection(_cs);
        conn.Open();
        return conn;
    }

    void Exec(string sql, params object?[] args) => Run(sql, args, c => c.ExecuteNonQuery());

    object? Scalar(string sql, params object?[] args) => Run(sql, args, c => c.ExecuteScalar());

    T Run<T>(string sql, object?[] args, Func<SqliteCommand, T> action)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + (char)('a' + i), args[i] ?? DBNull.Value);
        return action(cmd);
    }
}
