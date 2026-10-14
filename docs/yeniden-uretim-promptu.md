# Diyalog 360'ı sıfırdan yaz

Bankanın iş birimlerinden gelen Türkçe rapor isteklerini SQL'e çeviren, sorguyu güvenle çalıştıran ve sonucu grafik, tablo ve Türkçe özetle sunan sohbet tabanlı raporlama asistanı. UI metinleri, kod yorumları, README ve commit mesajları Türkçe olacak. Ses/STT özelliği yok. Temel ilke: **LLM yalnızca SQL önerir; neyin çalışacağına, özete ve grafiğe deterministik kod karar verir.**

## Yığın ve yapı
- `api/`: .NET 10 Minimal API, tek paket Microsoft.Data.Sqlite (SQLitePCL `raw` API'si de kullanılır). Port 5080. Derlenmiş önyüzü `wwwroot`'tan sunar. Komutlar: `dotnet run`, `-- seed`, `-- eval [id]`, `-- set-password [ad]`.
- `web/`: React 19, TypeScript, Vite, Tailwind 4, Recharts, oxlint. Build çıktısı `../api/wwwroot`. Geliştirmede `/api` istekleri 5080'e proxy'lenir.
- LLM: OpenAI uyumlu `/chat/completions` istemcisi. Ayarlar `Llm:BaseUrl, Model, ApiKey, Temperature=0, MaxTokens=900, TimeoutSeconds, LlamaCachePrompt, HealthUrl`. Anahtar hem `Authorization: Bearer` hem `api-key` başlığıyla gider. Varsayılan model yerel llama-server + Qwen3-30B-A3B-Instruct-2507 GGUF (`127.0.0.1:18080/v1`).
- `tests/` (xUnit), `eval/questions.json`.
- `scripts/`: `start-llm`, `dev`, `demo` (her biri .sh ve .ps1), `codespace.sh`.
- `.devcontainer`: dotnet 10 ve node 22 feature'ları, 5080 portu yönlendirilir, postAttach'ta `codespace.sh` çalışır.
  - `codespace.sh`: `Llm__*` ayarlarını GitHub Models'e (`https://models.github.ai/inference`, `openai/gpt-4.1-mini`, anahtar `GH_MODELS_TOKEN` ya da `GITHUB_TOKEN`, HealthUrl `.../catalog/models`, LlamaCachePrompt=false) yönlendirir. Açılışta modele test isteği atıp sonucu yazar. İlk açılışta giriş kullanıcısı tanımlatır. API'yi `0.0.0.0:5080`'de başlatır.
- README: macOS, Windows ve Codespaces için kurulum adımları, sorun giderme tabloları ve 6 adımlı demo senaryosu.

## Demo veritabanı (`Data/bank.db`)
Dosya yoksa açılışta üretilir. Üretim deterministiktir (`Random(360)`), tarihler bugüne göre son 24 ayı kapsar. Tablolarda FK ve index'ler vardır, üretim sonunda `ANALYZE` çalışır.

Tablolar:
- **Sube**(Id, Ad, Sehir, Bolge): 12 şube.
  - İstanbul: Kadıköy, Beşiktaş, Levent, Bakırköy
  - Ankara: Kızılay, Çankaya
  - İzmir: Alsancak, Karşıyaka
  - Bursa: Nilüfer; Antalya: Muratpaşa; Adana: Seyhan; Trabzon: Ortahisar
  - Müşteriler şubelere ağırlıklı dağıtılır.
- **Musteri**(Id, AdSoyad\*, TCKN\*, Telefon\*, Segment, SubeId, DogumYili, KayitTarihi): 5000 müşteri. \* işaretli kolonlar PII. Segment dağılımı: Bireysel %70, Premium %20, KOBİ %10.
- **Hesap**(MusteriId, HesapTuru, ParaBirimi, Bakiye, AcilisTarihi, Durum)
  - HesapTuru: Vadesiz, Vadeli, Döviz. ParaBirimi: TRY, USD, EUR.
  - Bakiye log-normal dağılır, segmente göre çarpılır. Hesapların %5'i Kapalı.
- **Kart**(MusteriId, KartTipi, KartLimiti, AcilisTarihi, Durum)
  - KartTipi (Classic, Gold, Platinum) segmente göre dağılır. Kartların %4'ü İptal.
  - Müşterilerin %35'inin 2 kartı var.
- **Sektor**: Market, Akaryakıt, Restoran, Giyim, Elektronik, Seyahat, Sağlık, Eğitim, E-ticaret, Eğlence. Her sektörün kendi ortalama tutarı ve ağırlığı var.
- **Islem**(KartId, SektorId, Tutar, IslemTipi, Kanal, IslemTarihi): yaklaşık 157 bin kayıt.
  - Tutar her zaman pozitif. IslemTipi: Harcama, %2 İade. Kanal: POS, İnternet, Mobil.
  - IslemTarihi metin olarak `YYYY-MM-DD HH:MM:SS` biçiminde.
  - İşlem sayısı kart tipine göre Poisson dağılımıyla üretilir.
- **Urun**: 10 ürün (Kategori: Ödeme, Kredi, Yatırım, Mevduat, Sigorta). POS Cihazı yalnızca KOBİ müşterilerine verilir.
- **MusteriUrun**(MusteriId, UrunId, BaslangicTarihi)
- **Kampanya**(Ad, SektorId, BaslangicTarihi, BitisTarihi, IndirimOrani): son 12 ayda 8 kampanya. Kampanya döneminde ilgili sektörün harcaması 1,5 katına çıkar.

Veriye konan bilinçli desenler:
- Seyahat yazın, Giyim ve Elektronik Kasım-Aralık'ta, Eğitim Eylül'de zirve yapar.
- E-ticaret her ay büyür.
- Alsancak şubesinin harcaması son 6 ayda düşer.
- Platinum kartların %25'i 4-8 aydır hiç işlem yapmıyor.

## Şema kataloğu
- Veritabanından tablo, kolon, tip, PK, FK ve satır sayısını okur.
- En fazla 12 farklı değeri olan TEXT kolonların değerlerini listeler. PII, PK, FK ve tarih kolonları hariç tutulur.
- Bunları `Prompt/schema.notes.json` dosyasındaki tablo/kolon açıklamaları ve `"pii": true` işaretleriyle birleştirir.
- Kompakt prompt metni üretir:
  - Tablo satırı: `### Tablo — açıklama (N satır)`
  - Kolon satırı: `- Kolon TİP PK FK→T.K [KİŞİSEL VERİ — SORGUDA KULLANMA] — açıklama · değerler: '…'`
  - Sonda "İlişkiler (JOIN koşulları)" listesi.
- `/api/schema` aynı bilgiyi önyüzdeki Veri kataloğu paneline verir.

## Prompt
Prompt metin dosyalarında durur, koda gömülmez; doğruluk bu dosyalar düzenlenerek artırılır.

Sistem prompt'u = `system.md` + şema + `glossary.md` + `examples.md`.
- Açılışta bir kez kurulur ve hiç değişmez; böylece llama KV önbelleğinde kalır.
- SHA256 parmak izi, cevap önbelleğinin sürümü olarak kullanılır.
- Her açılışta `Data/prompt.generated.md` dosyasına yazılır.

**system.md:**
- JSON'daki `tur` alanı:
  - `rapor`: istek veritabanından cevaplanabiliyor.
  - `netlestirme`: yalnızca sonucu belirgin biçimde değiştiren bir belirsizlik varsa; 2-4 seçenek sun. Belirsizlik küçükse varsayım yap.
  - `ret`: yazma/silme isteği, kişisel veri isteği ya da veritabanıyla ilgisiz istek.
- Yazım hatalarını ("yade" → iade, "kobi" → KOBİ) en yakın şema değerine yorumla ve bunu varsayımlara yaz.
- SQL kuralları:
  - SQLite; tek SELECT ya da WITH…SELECT; sonda noktalı virgül yok.
  - PII kolonlar hiçbir yerde kullanılmaz; müşteri `m.Id` ile gösterilir.
  - JOIN'ler yalnızca "İlişkiler" listesindeki koşullarla kurulur.
  - Tablo alias'ları: i, k, m, sb, s, h.
  - Sonuç kolonlarına Türkçe takma ad: `AS [Toplam Harcama]`.
  - Metin değerleri şemadakiyle birebir yazılır.
  - Tarih filtrelerinde her zaman `date('now','localtime',…)` kullanılır. Açık kalıplar: bu ay, geçen ay, son N gün, son N ay (= tamamlanmış son N takvim ayı), bu yıl, geçen yıl. Aylık kırılım için `strftime('%Y-%m')`.
  - Harcama hesaplarında `IslemTipi='Harcama'` filtresi unutulmaz.
  - Tekil cevaplı sorularda `LIMIT 1`; "en iyiler" gibi çoğul sorularda `LIMIT 10`.
  - Para `ROUND(,2)`, oran `ROUND(,1)`.
  - Sonuç gruplanarak özetlenir; kullanıcı istemedikçe ham işlem satırları listelenmez.
  - Takip sorusunda son SQL temel alınır ve yalnızca istenen değişiklik yapılır.
  - OR içeren koşullar paranteze alınır.
  - Kolon sırası: tarih, kategori, sayı.
- Diğer alanlar: `baslik` en fazla 8 kelime; `aciklama` iş diliyle 1-2 cümle; `varsayimlar` bir liste.

**glossary.md:**
- Harcama, iade ve net harcama tanımları.
- İşlem sayısı: işlem tipine göre filtre yok.
- Ortalama sepet.
- Aktif müşteri: son 90 günde harcaması olan. Pasif müşteri: `NOT EXISTS` ile.
- X kart sahibi: `COUNT(DISTINCT)`.
- Mevduat: Durum Aktif ve ParaBirimi TRY olan bakiyeler.
- Döviz hesabı: HesapTuru'na göre belirlenir.
- Kart ve hesap sayımlarında yalnızca Aktif kayıtlar.
- İşlemin şubesi: Islem → Kart → Musteri → Sube.
- Yaş hesabı.
- Kampanya harcaması için JOIN koşulu.
- Belirsiz terimlerde netleştirme sorulur: "en iyi müşteri", şube "performansı", "büyük müşteri".

**examples.md**, doğrulanmış 7 soru → JSON örneği:
1. Geçen ay sektörlere göre harcama.
2. Takip sorusu: "Bunu Ankara şubeleri için son 3 ay aylık göster".
3. "En iyi müşteriler" → netleştirme.
4. Netleştirme cevabı → Id, segment ve şehirle top 10.
5. İşlem yapmayan Gold kart sahipleri (`NOT EXISTS`).
6. Segmentlere göre müşteri sayısı ve TL mevduat.
7. Silme isteği → ret.

Kullanıcı mesajı iki bölümdür:
- `## Sohbet geçmişi`: son 6 mesaj; asistanın raporları `SQL: …` olarak, netleştirmeler seçenekleriyle birlikte yazılır.
- `## Yeni mesaj`

**LLM çıktısı:** `response_format` json_schema strict.
- Alanlar: `tur` (enum), `baslik`, `sql`, `aciklama`, `varsayimlar[]`, `netlestirme_sorusu`, `secenekler[]`. Hepsi zorunlu, `additionalProperties: false`.
- Yalnızca `LlamaCachePrompt` açıkken: istekte `cache_prompt: true` gönderilir ve açılışta sistem prompt'uyla ısınma isteği atılır.
- Sağlık kontrolü: `HealthUrl` ya da `{BaseUrl}/models` adresine GET, 3 sn zaman aşımı.

## Akış (ChatOrchestrator)
1. **Önbellek.** Anahtar = SHA256(tr-TR küçük harfe çevrilmiş ve noktalaması temizlenmiş soru + son SQL ya da son netleştirme sorusu + prompt sürümü).
2. **LLM** çağrısı.
3. Cevap `netlestirme` ya da `ret` ise doğrudan döndürülür.
4. Değilse SQL **SqlGuard**'dan geçer ve **QueryRunner** ile çalıştırılır.
   - **Invalid:** hata modele geri verilir ("Bu SQL çalıştırılamadı. Hata: … Düzeltilmiş JSON'u döndür."). En fazla 3 deneme.
   - **Forbidden:** düzeltme denenmez, istek engellenir.
5. Bozuk çıkan önbellek kaydı silinir. LLM hatasında ekranda ayrıntılı hata mesajı gösterilir.

Yanıt yapısı:
- `chatId, messageId`
- `kind`: report | clarify | blocked | error
- `title, text`
- `report`: `sql, explanation, assumptions, columns{name, kind}, rows, truncated, chart{type, x, y[], series}, tables, usedColumns, plan, planWarnings, dbMs`
- `clarify`: `question, options`
- `blocked`: `reason, layer, sql`
- `meta`: `attempts, fromCache, llmMs, totalMs, corrections[{sql, error}], model`

`/api/sql`: kullanıcının elle düzenlediği SQL, LLM'siz olarak aynı doğrulayıcıdan geçirilip çalıştırılır.

## Güvenlik
**QueryRunner:**
- Bağlantı `Mode=ReadOnly`.
- `sqlite3_limit`: LENGTH 1e6, SQL_LENGTH 20000.
- Progress handler ile 15 sn zaman aşımı; en fazla 1000 satır (fazlası `truncated`).
- `EXPLAIN QUERY PLAN` alınır. Alias'lar tablo adına çözülür; 50 binden fazla satırlı bir tabloda `SCAN` görülürse performans uyarısı verilir.
- Kolon türü çıkarılır: number, date, text.

**SqlGuard** (`sqlite3_set_authorizer` + `prepare_v2`):
- READ: yalnızca şemadaki tablolar ve `main` veritabanı.
- PII kolona erişim → Invalid; mesaj, modelin sorguyu düzeltebileceği şekilde yazılır.
- FUNCTION: beyaz liste (toplama, tarih, matematik, metin ve pencere fonksiyonları).
- SELECT ve RECURSIVE izinli; diğer her eylem → Forbidden.
- Tek ifade şartı (tail kontrolü) ve `sqlite3_stmt_readonly` kontrolü.
- Kullanılan tablo ve kolonları raporlar. Authorizer çalıştırma sırasında da bağlı kalır.

**KVKK:** LLM'e hiçbir satır verisi gönderilmez.

**Kimlik doğrulama:**
- Tek kullanıcı; bilgiler `Data/auth.json`'da. Şifre PBKDF2-SHA256 ile (600 bin iterasyon) saklanır, en az 8 karakter.
- Karşılaştırmalar sabit zamanlı. 5 hatalı denemede 5 dakika kilit. Login uç noktasında 10 istek/dk sınırı.
- Oturum çerezi: HttpOnly, SameSite=Strict.
- Oturumlar sunucu tarafında da `Session` tablosunda tutulur. Çerezde şifre dosyasının parmak izi bulunur; şifre değişince tüm oturumlar düşer.
- DataProtection anahtarları `Data/keys` altında.
- Yetkisiz `/api` çağrıları yönlendirme yerine 401 döner. Auth uçları dışındaki tüm `/api` uçları giriş ister.

**HTTP başlıkları:** CSP (`self`), nosniff, X-Frame-Options DENY, no-referrer; `/api` cevaplarında `no-store`.

**Girdi sınırları:** chatId `^[A-Za-z0-9-]{1,64}$`, mesaj en fazla 1000 karakter, SQL en fazla 5000 karakter.

## Rapor (ReportBuilder, LLM'siz)
Adı No/Id/Numara/Kod/Yıl ile biten sayısal kolonlar etiket sayılır.

Grafik seçimi:
- Sonuç boşsa ya da sayısal kolon yoksa → tablo
- 1 satır ve en fazla 4 sayı → kpi
- 1 tarih kolonu + sayılar → line
- 1 tarih + 1 metin kolonu (en fazla 12 seri) → multiline
- Tarih yok, metin kolonu var, en fazla 30 tekil satır → bar
- Diğer durumlar → tablo

Türkçe özet tr-TR biçimiyle kodda yazılır (para kolonlarında "TL", oranlarda "%"):
- bar: en yüksek ve en düşük; toplanabilir ölçülerde toplam ve liderin payı.
- line: ilk ve son dönem arasındaki % değişim, zirve dönem.
- multiline: en yüksek ve en düşük seri, en çok artan ve en çok düşen seri.
- kpi: değerlerin kendisi.
- Sonuç boşsa ya da kesilmişse bunu belirten not.

## Depolama (`app.db`, WAL)
- `Chat`
- `Message`: asistan cevabı JSON olarak saklanır.
- `LlmCache`: Key, Question, Answer, Hits.
- `Session`
- `QueryLog`: soru, tür, SQL, satır sayısı, deneme sayısı, önbellekten mi, llm/db/toplam ms, hata. Sohbet silinse de silinmez.

## Önyüz
**Giriş ekranı.**

**Üst bar:**
- "Geçmiş Sorgularım" çekmecesini açan menü düğmesi.
- "360" logosu.
- Model durum rozeti (15 sn'de bir sağlık kontrolü).
- Veri kataloğu paneli: tablolar, kolonlar, PII/FK işaretleri, değer listeleri.
- Yeni sohbet düğmesi.
- Kullanıcı adı ve Çıkış.

**Boş ekran:** 6 öneri sorusu.

**ReportCard:**
- Başlık ve özet.
- Rozetler: önbellekten / N kez kendini düzeltti / performans uyarısı / salt okunur ✓.
- Sekmeler: Grafik | Tablo (N) | SQL.
- Grafik türü değiştirme: bar ↔ halka (en fazla 8 pozitif değer), line ↔ bar.
- Birden çok metrik varsa metrik seçici.
- CSV indirme: BOM'lu, `;` ayraçlı, virgüllü ondalık (Excel uyumlu).

**Diğer kartlar:**
- Netleştirme kartı: tıklanabilir seçenekler; yalnızca son mesajda aktif.
- Engellendi kartı: isteği durduran katman ve SQL.
- Hata kartı.

**SQL sekmesi:**
- Açıklama ve varsayımlar.
- Biçimlendirilmiş SQL ve Kopyala düğmesi.
- "SQL'i düzenle ve çalıştır".
- Kullanılan tablolar.
- Süreler: model, veritabanı, toplam, deneme sayısı.
- Kendini düzeltme geçmişi.
- Sorgu planı ve uyarılar.

**Bekleme göstergesi:** hangi adımda olunduğu ve geçen saniye.

**Tablo:** sütun başlığına tıklayınca sıralanır; sayılar sağa hizalı ve tr-TR biçimli; dönem değerleri "Eyl 2026" biçiminde.

**Grafik:** 6'dan fazla kategoride yatay bar, kompakt eksen etiketleri, sabit renk paleti.

**Geçmiş:**
- Konuşmalar localStorage'da tutulur: rapor başına en fazla 200 satır; kota dolarsa en eski konuşma atılır.
- Konuşma açılınca sunucudan tazelenir.
- Silme işlemi sunucudaki kaydı da siler.

## Doğrulama
**SqlGuard testleri:**
- Okuma sorguları geçer.
- Forbidden olmalı: DELETE, DROP, UPDATE, INSERT, `;` ile ikinci ifade, PRAGMA, ATTACH, CREATE, BEGIN.
- PII kolonu SELECT'te, WHERE'de ya da `*` ile kullanılırsa engellenir.
- Invalid olmalı: `sqlite_schema`, `load_extension`, olmayan kolon, iki SELECT, yazım hatalı SQL.
- Dev `printf` sorgusu bellek sınırına takılır.
- Kullanılan tablo/kolon raporu, satır limiti ve plan uyarısı doğru çalışır.
- `examples.md`'deki tüm SQL'ler çalışır ve sonuç döndürür.

**Auth testleri:**
- Dosya yokken giriş yapılamaz.
- Kullanıcı adı büyük/küçük harfe duyarsız; yanlış bilgiyle giriş yapılamaz.
- Şifre dosyada düz metin olarak durmaz; kısa şifre reddedilir.
- Kilit ve kilidin süre dolunca açılması (FakeTimeProvider ile).

**Eval:** `eval/questions.json` (24 soru; alanlar: id, soru, beklenen_tur, beklenen_sql, onceki[]) önbellek kullanılmadan çalıştırılır.
- Sonuç kümesi kolon adı ve sırasından bağımsız karşılaştırılır.
- Satır sayısı eşit olmalı; beklenen her satırın değerleri üretilen bir satırda bulunmalı (1 ondalık hassasiyet).
- Sonuçlar `eval/results.json`'a yazılır.

**Bitirme ölçütü:** build, test ve lint temiz geçmeli; demo senaryosu baştan sona çalışmalı:
1. Sektörlere göre harcama
2. İstanbul şubeleri için aylık takip sorusu
3. "En iyi müşteriler" netleştirmesi
4. İşlem yapmayan Platinum kart sahipleri
5. Silme isteğinin reddi; SQL panelinde DELETE'in engellenmesi
6. SQL panelinin gösterilmesi
