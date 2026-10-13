# Diyalog 360 — Sıfırdan üretim prompt'u

Aşağıdaki metni bir kodlama asistanına (örn. Claude Code) boş bir klasörde verin.

---

**Görev:** "Diyalog 360" adlı, Türkçe soruları SQL'e çevirip güvenle çalıştıran ve sonucu grafik + tablo + Türkçe özetle
sunan sohbet tabanlı raporlama uygulamasını sıfırdan yaz. Az kod, sade yapı; her modül tek sorumluluklu. Arayüz ve
mesajlar Türkçe. Önce mimariyi kur, sonra aşağıdaki sırayla ilerle; her adımda derle ve test et.

**Yığın:** .NET 10 Minimal API (`api/`), React + TypeScript + Vite + Tailwind + Recharts (`web/`, build çıktısı `api/wwwroot`),
xUnit (`tests/`), eval seti (`eval/`). Uygulama verisi (sohbet, önbellek, oturum, denetim logu) SQLite `app.db`.

## Mimari

```
Tarayıcı ──HTTP──► API ─► ChatOrchestrator ─► PromptBuilder ─► ILlmClient ─────────► (llama-server | Azure OpenAI/Foundry)
                          │                                    JSON şemalı cevap
                          ├► IReportDatabase.Validate + Execute ─► (SQLite | Azure SQL)   ◄─ SchemaCatalog (şema okuma)
                          ├► ReportBuilder (grafik + özet, LLM'siz)
                          └► AppStore (app.db)
```

**Tak-çıkar sağlayıcılar** (yapılandırmayla seçilir, kod değişmez):

1. `IReportDatabase` — `Dialect`, `ReadSchema()` (tablo, kolon, tip, PK/FK, satır sayısı, ≤12 farklı değerli metin kolonlarının değerleri),
   `Validate(sql)` → `Ok | Invalid(mesaj) | Forbidden(mesaj)`, `Execute(sql)` (salt-okuma, zaman aşımı, satır limiti),
   `ExplainPlan(sql)` (index'siz büyük tablo taraması uyarısı).
   - **SqliteReportDatabase:** `Mode=ReadOnly`; `sqlite3_set_authorizer` ile AST düzeyinde izin listesi (yalnızca SELECT/READ/izinli
     fonksiyonlar; yazma, PRAGMA, ATTACH → Forbidden); tek ifade (`tail`) ve `sqlite3_stmt_readonly` kontrolü; PII kolonu okuması
     reddedilir; progress handler ile zaman aşımı; `SQLITE_LIMIT_LENGTH` 1 MB; `EXPLAIN QUERY PLAN`.
   - **AzureSqlReportDatabase:** `Microsoft.Data.SqlClient`, `ApplicationIntent=ReadOnly`, `db_datareader` yetkili kullanıcı ya da
     Managed Identity / Entra ID; ScriptDom (`TSql160Parser`) ile tek `SelectStatement`, `INTO` yok, OPENROWSET/OPENQUERY/linked
     server/başka DB/`sys` şeması yasak; şema `INFORMATION_SCHEMA` + `sys.foreign_keys`; PII kolonlarına DB'de `DENY SELECT`;
     `sp_describe_first_result_set` ile kolon doğrulama; `SET SHOWPLAN_XML ON` ile plan ve `MissingIndex` önerisi; `CommandTimeout`.
2. `ILlmClient` — OpenAI uyumlu `/chat/completions`, `response_format: json_schema (strict)`, `temperature 0`.
   Ayarlar: `BaseUrl`, `Model` (Azure'da deployment adı), `ApiKey` veya `UseEntraId` (`DefaultAzureCredential`, kapsam
   `https://cognitiveservices.azure.com/.default`), `LlamaCachePrompt` (yalnızca llama-server). Yerel: `http://127.0.0.1:18080/v1`
   (Qwen3-30B-A3B-Instruct GGUF). Azure: `https://<kaynak>.openai.azure.com/openai/v1`. Ajan servisi kullanma; akış kendi kodumuzda.
3. **Lehçeye göre prompt:** `Prompt/{sqlite|tsql}/system.md` ve `examples.md` (tarih kalıpları, `LIMIT`/`TOP`); `glossary.md` ve
   `schema.notes.json` (Türkçe açıklamalar, `"pii": true`) ortak.

`appsettings.json`: `ReportDb: { Provider: "Sqlite" | "AzureSql", ConnectionString }`, `Llm: { BaseUrl, Model, ApiKey, UseEntraId, LlamaCachePrompt }`,
`Query: { MaxRows: 1000, TimeoutSeconds: 15 }`. Sırlar repoya girmez (user-secrets / ortam değişkeni / Key Vault).

## Akış (ChatOrchestrator)

1. Geçmişten son 6 mesaj (önceki SQL'ler dahil) + yeni mesaj. Önbellek anahtarı = SHA256(normalize(mesaj) + son SQL + sistem prompt hash'i).
2. Sistem prompt'u **sabit**: kurallar + kompakt şema (`Tablo(Kolon TİP PK/FK→… [KİŞİSEL VERİ] — açıklama · değerler: …)` + JOIN listesi)
   + iş sözlüğü + doğrulanmış örnekler. Bugünün tarihini yazma; SQL'de `date('now')`/`GETDATE()` kullan (önek önbelleği bozulmasın).
   Açılışta bir ısınma çağrısı yap.
3. LLM tek çağrıda JSON döner: `{tur: rapor|netlestirme|ret, baslik, sql, aciklama, varsayimlar[], netlestirme_sorusu, secenekler[]}`.
4. `rapor` → Validate → Execute. `Invalid` ise hata mesajını LLM'e geri ver, en fazla 2 düzeltme. `Forbidden` ise düzeltme deneme,
   "engellendi" kartı + durduran katman. Model kapalıysa önbellekten cevap ver.
5. ReportBuilder: kural tabanlı grafik (1 satır→KPI; tarih+sayı→çizgi; tarih+kategori+sayı→çok serili; kategori+sayı≤30→çubuk;
   diğer→tablo) ve sayıları kodda hesaplanan şablonlu Türkçe özet (tr-TR biçim).
6. Her istek `QueryLog`'a: soru, SQL, tür, satır, deneme, süreler, hata.

## Güvenlik

- **Tek kullanıcı girişi:** `dotnet run -- set-password` → `Data/auth.json` (PBKDF2-SHA256 600k tur, tuz; dosya izni 600; repoya girmez).
  Sabit süreli karşılaştırma, genel hata mesajı, 5 hatada 5 dk kilit, giriş ucunda dakikada 10 istek.
- **Oturum:** cookie auth, `HttpOnly`, `SameSite=Strict`, 8 saat; Data Protection anahtarları diskte. Oturum kimliği sunucuda
  (`Session` tablosu) + şifre parmak izi çerezde; `OnValidatePrincipal` ile kontrol → çıkış ve şifre değişimi eski çerezleri geçersiz kılar.
- Tüm `/api` uçları `RequireAuthorization` (401, yönlendirme yok); `/api/auth/login|logout|me` anonim.
- CSP `default-src 'self'`, `X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy: no-referrer`, API'de `no-store`.
- Girdi sınırları (mesaj ≤1000, SQL ≤5000, `chatId` regex). LLM'e satır verisi gönderme.

## API

`POST /api/auth/login|logout`, `GET /api/auth/me`, `POST /api/chat {chatId?, message}`, `POST /api/sql {chatId?, sql}` (elle düzenlenen SQL,
aynı doğrulama), `GET /api/history/{chatId}`, `DELETE /api/chats/{chatId}`, `GET /api/schema`, `GET /api/health`.
CLI: `set-password`, `seed`, `eval [id]`.

## Önyüz

Giriş ekranı; üst çubukta model durumu, Veri kataloğu, Yeni sohbet, kullanıcı adı + Çıkış. Sol hamburger menü **Geçmiş Sorgularım**:
konuşmalar `localStorage`'da (rapor başına ≤200 satır, kota dolarsa en eskiler silinir); tıklayınca açılır, tam veri `/api/history` ile
tazelenir ve konuşma aynı `chatId` ile devam eder; çöp kutusu ikonu + `confirm` ile hem yerelden hem sunucudan silinir.
Rapor kartı: başlık, özet, rozetler (önbellekten, kendini düzeltti, performans uyarısı), Grafik/Tablo/SQL sekmeleri, CSV (UTF-8 BOM, `;`).
SQL paneli: açıklama, varsayımlar, SQL + kopyala + düzenle ve çalıştır, okunan tablolar, süreler, düzeltme adımları, sorgu planı.
Netleştirme kartında seçenek butonları; engellendi kartında durduran katman. 401 alınca giriş ekranına dön.

## Demo verisi ve kalite

- Seeder (sabit tohum, tarihler bugüne göreli, son 24 ay): Sube, Musteri (PII: AdSoyad, TCKN, Telefon), Hesap, Kart, Sektor, Islem
  (~150 bin), Urun, MusteriUrun, Kampanya; FK ve tarih index'leri. Bilinçli desenler: yaz seyahat zirvesi, Kasım-Aralık elektronik,
  büyüyen e-ticaret, son 6 ayda düşen bir şube, kullanılmayan Platinum kartlar, kampanya artışları.
- Testler: doğrulayıcı (izin/ret/PII/bellek sınırı/tek ifade), giriş (doğru/yanlış/kilit/özet), `examples.md` SQL'lerinin çalışması.
- Eval: ≥24 soru (`rapor` için beklenen SQL; sonuç kümesi kolon adından bağımsız karşılaştırılır; `netlestirme`/`ret` için tür),
  takip soruları `onceki` alanıyla. Doğruluk kodla değil prompt dosyalarıyla iyileştirilir.
- Scriptler: `start-llm`, `dev`, `demo` (`.sh` macOS/Linux, `.ps1` Windows). README: macOS + Windows kurulum, çalıştırma, sorun giderme.

**Bitti tanımı:** testler yeşil; eval ≥%90; giriş/çıkış ve geçmiş menüsü tarayıcıda çalışıyor; `ReportDb:Provider` ve `Llm:BaseUrl`
değiştirilerek kod değişmeden Azure SQL ve Azure LLM'e geçilebiliyor.
