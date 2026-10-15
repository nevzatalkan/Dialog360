using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Diyalog360.Api.Services;

public record StoredMessage(long Id, string Role, string Content, string CreatedAt);

/// <summary>Geçmiş menüsündeki bir sohbet: başlık, zamanlar ve özet sayılar.</summary>
public record ChatSummary(string Id, string Title, string CreatedAt, string UpdatedAt, long Questions, long Reports, string? Snippet = null);

/// <summary>
/// Uygulama verisi (app.db): sohbet geçmişi, LLM cevap önbelleği, denetim logu ve giriş kullanıcıları.
/// Rapor sorguları yalnızca bank.db üzerinde çalışır; bu veritabanı (ve AppUser tablosu) şema kataloğunda yoktur,
/// LLM'e gösterilmez ve SQL doğrulayıcı tarafından erişilemez.
/// </summary>
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
            CREATE TABLE IF NOT EXISTS AppUser (Id INTEGER PRIMARY KEY, Username TEXT NOT NULL UNIQUE COLLATE NOCASE,
                PasswordHash TEXT NOT NULL, CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')), LastLoginAt TEXT);
            """);
        // Eski app.db dosyalarına geçmiş menüsü için gereken kolonları ekle (veri kaybı olmadan)
        var chatColumns = Run("SELECT name FROM pragma_table_info('Chat')", [], c =>
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var r = c.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
            return names;
        });
        if (!chatColumns.Contains("Title")) Exec("ALTER TABLE Chat ADD COLUMN Title TEXT");
        if (!chatColumns.Contains("Owner")) Exec("ALTER TABLE Chat ADD COLUMN Owner TEXT");
        if (!chatColumns.Contains("UpdatedAt"))
        {
            Exec("ALTER TABLE Chat ADD COLUMN UpdatedAt TEXT");
            Exec("UPDATE Chat SET UpdatedAt = COALESCE((SELECT MAX(CreatedAt) FROM Message WHERE Message.ChatId = Chat.Id), CreatedAt)");
        }
        Exec("CREATE INDEX IF NOT EXISTS IX_Chat_UpdatedAt ON Chat(UpdatedAt)");

        var messageColumns = Run("SELECT name FROM pragma_table_info('Message')", [], c =>
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var r = c.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
            return names;
        });
        if (!messageColumns.Contains("SearchText")) Exec("ALTER TABLE Message ADD COLUMN SearchText TEXT");
        BackfillSearchText();

        // Tek kullanıcı: tablo boşsa varsayılan yönetici hesabı oluşturulur (şifre düz metin değil, PBKDF2 hash olarak saklanır)
        if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM AppUser")) == 0)
            Exec("INSERT INTO AppUser (Username, PasswordHash) VALUES ($a, $b)", "admin", HashPassword("admin"));
    }

    /// <summary>Kullanıcı adı/şifre doğruysa true döner ve son giriş zamanını günceller.</summary>
    public bool ValidateUser(string username, string password)
    {
        var stored = Scalar("SELECT PasswordHash FROM AppUser WHERE Username = $a", username) as string;
        // Kullanıcı yoksa da aynı maliyette doğrulama yapılır; yanıt süresinden kullanıcı adı tahmin edilemez
        var ok = VerifyPassword(password, stored ?? DummyHash) && stored != null;
        if (ok) Exec("UPDATE AppUser SET LastLoginAt = datetime('now','localtime') WHERE Username = $a", username);
        return ok;
    }

    const int Iterations = 100_000;
    static readonly string DummyHash = HashPassword(Guid.NewGuid().ToString());

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public string EnsureChat(string? chatId)
    {
        chatId = string.IsNullOrWhiteSpace(chatId) ? Guid.NewGuid().ToString("N")[..12] : chatId;
        Exec("INSERT OR IGNORE INTO Chat (Id) VALUES ($a)", chatId);
        return chatId;
    }

    public long AddMessage(string chatId, string role, string content)
    {
        var id = (long)Scalar("INSERT INTO Message (ChatId, Role, Content, SearchText) VALUES ($a, $b, $c, $d) RETURNING Id", chatId, role, content, BuildSearchText(role, content))!;
        Exec("UPDATE Chat SET UpdatedAt = datetime('now','localtime') WHERE Id = $a", chatId);
        return id;
    }

    // ---- Geçmiş menüsü -------------------------------------------------------------------------

    /// <summary>Sohbeti, sahibi henüz yoksa giriş yapan kullanıcıya bağlar.</summary>
    public void ClaimChat(string chatId, string owner) =>
        Exec("UPDATE Chat SET Owner = $b WHERE Id = $a AND Owner IS NULL", chatId, owner);

    /// <summary>
    /// Kullanıcı bu sohbete erişebilir mi? Başka bir kullanıcıya ait sohbetler reddedilir; sahipsiz (eski) sohbetler ve
    /// henüz var olmayan sohbet kimlikleri (yeni sohbet olarak açılır) serbesttir.
    /// </summary>
    public bool CanAccess(string chatId, string owner) =>
        Convert.ToInt64(Scalar("SELECT COUNT(*) FROM Chat WHERE Id = $a AND Owner IS NOT NULL AND Owner <> $b COLLATE NOCASE", chatId, owner)) == 0;

    const string ListChatsSql = """
        SELECT c.Id,
               COALESCE(NULLIF(TRIM(c.Title), ''),
                        (SELECT CASE WHEN m.Content LIKE '[Elle düzenlenen SQL]%' THEN 'SQL: ' || TRIM(REPLACE(m.Content, '[Elle düzenlenen SQL]', ''), char(10) || char(13) || ' ') ELSE m.Content END
                         FROM Message m WHERE m.ChatId = c.Id AND m.Role = 'user' ORDER BY m.Id LIMIT 1)) AS Title,
               c.CreatedAt,
               COALESCE(c.UpdatedAt, c.CreatedAt) AS UpdatedAt,
               (SELECT COUNT(*) FROM Message m WHERE m.ChatId = c.Id AND m.Role = 'user') AS Questions,
               (SELECT COUNT(*) FROM Message m WHERE m.ChatId = c.Id AND m.Role = 'assistant'
                  AND json_extract(m.Content, '$.kind') = 'report') AS Reports
        FROM Chat c
        WHERE c.Id NOT LIKE 'eval-%'
          AND (c.Owner IS NULL OR c.Owner = $a COLLATE NOCASE)
          AND EXISTS (SELECT 1 FROM Message m WHERE m.ChatId = c.Id)
          AND ($b IS NULL OR instr(fold(COALESCE(c.Title, '')), $b) > 0
               OR EXISTS (SELECT 1 FROM Message m WHERE m.ChatId = c.Id AND instr(fold(m.SearchText), $b) > 0))
        ORDER BY COALESCE(c.UpdatedAt, c.CreatedAt) DESC, c.rowid DESC
        LIMIT $c
        """;

    /// <summary>Kullanıcının sohbetleri, son etkinliğe göre yeniden eskiye. Eval sohbetleri ve boş sohbetler listelenmez.</summary>
    /// <summary>
    /// Kullanıcının sohbetleri, son etkinliğe göre yeniden eskiye. Arama verilirse konuşmanın tamamında (sorular, rapor başlığı ve özeti,
    /// SQL, açıklama, varsayımlar, tablo değerleri, netleştirme ve ret metinleri) büyük/küçük harf ve Türkçe karakter duyarsız arar
    /// ve her sonuca eşleşmenin geçtiği kısa bir alıntı ekler.
    /// </summary>
    public List<ChatSummary> ListChats(string owner, string? search, int limit = 300)
    {
        var q = string.IsNullOrWhiteSpace(search) ? null : Fold(search.Trim());
        var list = Run(ListChatsSql, [owner, q, limit], cmd =>
        {
            var rows = new List<ChatSummary>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new ChatSummary(r.GetString(0), r.IsDBNull(1) ? "Adsız sohbet" : r.GetString(1),
                    r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5)));
            return rows;
        });
        if (q == null) return list;
        return list.Select(c =>
        {
            if (Fold(c.Title).Contains(q, StringComparison.Ordinal)) return c;
            var text = Run("SELECT SearchText FROM Message WHERE ChatId = $a AND instr(fold(SearchText), $b) > 0 ORDER BY Id LIMIT 1",
                [c.Id, q], cmd => cmd.ExecuteScalar() as string);
            return text == null ? c : c with { Snippet = Snippet(text, q) };
        }).ToList();
    }

    void BackfillSearchText()
    {
        var pending = Run("SELECT Id, Role, Content FROM Message WHERE SearchText IS NULL", [], cmd =>
        {
            var rows = new List<(long Id, string Role, string Content)>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
            return rows;
        });
        if (pending.Count == 0) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Message SET SearchText = $t WHERE Id = $id";
        var pt = cmd.Parameters.Add("$t", SqliteType.Text);
        var pid = cmd.Parameters.Add("$id", SqliteType.Integer);
        foreach (var (id, role, content) in pending) { pt.Value = BuildSearchText(role, content); pid.Value = id; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }


    public bool RenameChat(string chatId, string owner, string title) =>
        Run("UPDATE Chat SET Title = $c WHERE Id = $a AND (Owner IS NULL OR Owner = $b COLLATE NOCASE)",
            [chatId, owner, title], c => c.ExecuteNonQuery()) > 0;

    /// <summary>Sohbeti ve mesajlarını siler. Denetim logu (QueryLog) bilinçli olarak korunur.</summary>
    public bool DeleteChat(string chatId, string owner)
    {
        if (!CanAccess(chatId, owner)) return false;
        Exec("DELETE FROM Message WHERE ChatId = $a", chatId);
        Exec("DELETE FROM Chat WHERE Id = $a", chatId);
        return true;
    }

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
        conn.CreateFunction("fold", (string? s) => s == null ? null : Fold(s), isDeterministic: true);
        return conn;
    }

    // ---- Arama ----------------------------------------------------------------------------------
    // Asistan cevapları JSON olarak saklanır (Türkçe karakterler kaçışlı, alan adları metnin içinde). Bu yüzden her mesaj için
    // yalnızca kullanıcının gördüğü değerlerden oluşan düz bir arama metni (Message.SearchText) tutulur.

    const int SearchRowLimit = 300;
    static readonly System.Globalization.CultureInfo Tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Büyük/küçük harf ve Türkçe karakter duyarsız karşılaştırma için katlama: "KADIKÖY", "Kadıköy" → "kadikoy". Uzunluğu korur.</summary>
    public static string Fold(string s)
    {
        var lower = s.ToLower(Tr);
        if (lower.Length != s.Length) lower = s.ToLowerInvariant(); // eşleşme konumları orijinal metne uygulanabilsin
        var chars = lower.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            chars[i] = chars[i] switch { 'ı' => 'i', 'ş' => 's', 'ğ' => 'g', 'ü' => 'u', 'ö' => 'o', 'ç' => 'c', 'â' => 'a', 'î' => 'i', 'û' => 'u', _ => chars[i] };
        return new string(chars);
    }

    /// <summary>Bir mesajın aranabilir düz metni: soru ya da cevabın kullanıcıya görünen tüm değerleri.</summary>
    public static string BuildSearchText(string role, string content)
    {
        if (role == "user")
            return content.StartsWith("[Elle düzenlenen SQL]") ? content["[Elle düzenlenen SQL]".Length..].Trim()
                : content.StartsWith("[Detay] ") ? content["[Detay] ".Length..] : content;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            var root = doc.RootElement;
            var parts = new List<string>();
            void Add(System.Text.Json.JsonElement e, string name)
            {
                if (e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                    parts.Add(v.GetString()!);
            }
            void AddAll(System.Text.Json.JsonElement arr)
            {
                if (arr.ValueKind != System.Text.Json.JsonValueKind.Array) return;
                foreach (var x in arr.EnumerateArray())
                    if (x.ValueKind is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number) parts.Add(x.ToString());
            }
            Add(root, "title"); Add(root, "text");
            if (root.TryGetProperty("report", out var rep) && rep.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                Add(rep, "sql"); Add(rep, "explanation");
                if (rep.TryGetProperty("assumptions", out var asm)) AddAll(asm);
                if (rep.TryGetProperty("columns", out var cols) && cols.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var c in cols.EnumerateArray()) Add(c, "name");
                if (rep.TryGetProperty("rows", out var rows) && rows.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var row in rows.EnumerateArray().Take(SearchRowLimit)) { AddAll(row); parts.Add(""); }
            }
            if (root.TryGetProperty("clarify", out var cl) && cl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                Add(cl, "question");
                if (cl.TryGetProperty("options", out var opts)) AddAll(opts);
            }
            if (root.TryGetProperty("blocked", out var bl) && bl.ValueKind == System.Text.Json.JsonValueKind.Object) { Add(bl, "reason"); Add(bl, "sql"); }
            return string.Join("\n", parts.Where(p => p != null));
        }
        catch (System.Text.Json.JsonException)
        {
            return content;
        }
    }

    /// <summary>Eşleşmenin çevresinden kısa bir alıntı: "…Kadıköy şubesinin son 6 aylık…"</summary>
    static string? Snippet(string text, string foldedQuery)
    {
        var folded = Fold(text);
        int i = folded.IndexOf(foldedQuery, StringComparison.Ordinal);
        if (i < 0) return null;
        int start = Math.Max(0, i - 40), end = Math.Min(text.Length, i + foldedQuery.Length + 60);
        var s = string.Join(' ', text[start..end].Split((char[])['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return (start > 0 ? "…" : "") + s + (end < text.Length ? "…" : "");
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
