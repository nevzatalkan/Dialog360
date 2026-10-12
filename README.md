# Diyalog 360

Türkçe sorulan rapor isteğini veritabanı şemasını bilerek SQL'e çeviren, sorguyu güvenle çalıştıran ve sonucu
grafik, tablo ve Türkçe özetle sunan sohbet tabanlı raporlama asistanı. Tamamen yerel çalışır (Qwen + llama.cpp).

## Çalıştırma

Gereksinimler: .NET 10 SDK, Node 20+, `llama-server` (llama.cpp), bir Qwen GGUF modeli.

```bash
scripts/start-llm.sh        # 1. terminal: yerel model → http://127.0.0.1:18080/v1
scripts/demo.sh             # 2. terminal: önyüzü derler, her şeyi http://localhost:5080 adresinde sunar
```

Geliştirme için `scripts/dev.sh` (API 5080 + Vite 5173, sıcak yenileme).

İlk açılışta `api/Data/bank.db` demo veritabanı (5.000 müşteri, ~157.000 kart işlemi) bugüne göre üretilir.
Demo gününden önce tarihleri tazelemek için: `cd api && dotnet run -- seed`.

## Akış

```
Soru ──► Önbellek (aynı soru + aynı bağlam) ──► Yerel LLM (JSON şemalı çıktı: rapor | netlestirme | ret)
                                                   │
             ┌─────────────── hata mesajı (en fazla 2 düzeltme) ◄──┐
             ▼                                                     │
        SqlGuard (SQLite authorizer) ──► QueryRunner (read-only, 15 sn, 1000 satır) ──► Grafik kuralı + Türkçe özet (kodda)
```

- **LLM yalnızca SQL önerir**; neyin çalışacağına deterministik kod karar verir.
- **Sabit prompt öneki** (kurallar + şema + sözlük + örnekler, ~4.800 token) açılışta modele bir kez okutulur;
  sonraki sorularda llama-server KV önbelleğinden gelir, yalnızca soru ve sohbet geçmişi işlenir.
- **Özet ve grafik seçimi LLM'siz**: sayılar kodda hesaplanır, model sayı uyduramaz.
- **Takip soruları**: son 6 mesaj (önceki SQL'ler dahil) prompt'a eklenir; "bunu şubeye göre kır" önceki SQL'i değiştirir.

## Güvenlik katmanları

| Katman | Ne yapar |
|---|---|
| Bağlantı | Rapor veritabanı `Mode=ReadOnly` ile açılır |
| SqlGuard | SQLite'ın kendi parser'ı sorguyu derlerken her erişim için authorizer'ı çağırır: yalnızca SELECT, şemadaki tablolar ve izinli fonksiyonlar geçer; INSERT/UPDATE/DELETE/DROP/PRAGMA/ATTACH reddedilir; tek ifade ve `sqlite3_stmt_readonly` kontrol edilir |
| KVKK | PII kolonları (`schema.notes.json`'da `"pii": true`) SELECT, WHERE, JOIN dahil hiçbir yerde okunamaz; LLM'e satır verisi gönderilmez |
| Çalıştırma | 15 sn zaman aşımı (progress handler), 1000 satır limiti, sorgu planında index'siz büyük tablo taraması uyarısı |
| Denetim | Her soru, SQL, süre, deneme sayısı `app.db` → `QueryLog` tablosunda |

SQL panelindeki **"SQL'i düzenle ve çalıştır"** ile `DELETE FROM Musteri` yazıp doğrulayıcının engellediği canlı gösterilebilir.

## Doğruluğu artırmak: kod değil, metin

| Dosya | İçerik |
|---|---|
| `api/Prompt/system.md` | Kurallar (SQLite tarih kalıpları, takma adlar, netleştirme/ret ölçütleri) |
| `api/Prompt/schema.notes.json` | Tablo/kolon açıklamaları ve PII işaretleri; yapısal kısım (kolon, tip, FK, değer listeleri) DB'den otomatik okunur |
| `api/Prompt/glossary.md` | İş sözlüğü ("aktif müşteri", "mevduat", "kampanya harcaması"...) |
| `api/Prompt/examples.md` | Doğrulanmış soru → JSON örnekleri |
| `eval/questions.json` | Soru + beklenen SQL; sonuç kümeleri karşılaştırılır |

```bash
cd api && dotnet run -- eval        # tüm eval seti (önbelleksiz), sonuç eval/results.json
cd api && dotnet run -- eval q18    # tek soru
dotnet test tests                   # SqlGuard güvenlik testleri + örnek SQL'lerin geçerliliği
```

Birleşik prompt her açılışta `api/Data/prompt.generated.md` dosyasına yazılır.

## Demo senaryosu (~3 dk)

1. "Geçen ay sektörlere göre toplam kart harcaması" → temel akış, çubuk grafik
2. "Bunu İstanbul şubeleri için son 6 ay aylık göster" → takip sorusu, 4 tablo join, çok serili çizgi
3. "En iyi müşterilerimiz kimler?" → netleştirme; seçenek tıklanır, PII'sız top-10
4. "Son 90 günde hiç işlem yapmayan Platinum kart sahibi sayısı" → NOT EXISTS, gösterge kartı
5. "Müşteri tablosunu sil" → model reddeder; SQL panelinde `DELETE` yazılır → doğrulayıcı engeller
6. SQL panelini aç → açıklama, varsayımlar, kullanılan tablolar, süreler, sorgu planı

Demo öncesi senaryoyu bir kez koşturun: cevaplar önbelleğe girer, canlı demoda anında ve model kapalıyken bile gelir.

## Yapı

```
api/        .NET 10 Minimal API
  Services/ SqlGuard, QueryRunner, SchemaCatalog, PromptBuilder, LlmClient, ChatOrchestrator, ReportBuilder, AppStore, EvalRunner
  Data/     DemoSeeder + bank.db (rapor verisi) + app.db (sohbet, önbellek, log)
  Prompt/   LLM'e giden metinler
web/        React + TypeScript + Vite + Tailwind + Recharts
eval/       Doğruluk ölçüm seti
tests/      xUnit güvenlik testleri
scripts/    start-llm.sh, dev.sh, demo.sh
```

LLM istemcisi OpenAI uyumludur; `appsettings.json` → `Llm:BaseUrl/Model/ApiKey` değiştirilerek Azure OpenAI vb. bir servise geçilebilir
(llama'ya özel `LlamaCachePrompt` ayarını `false` yapın).
