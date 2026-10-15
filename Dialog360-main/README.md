# Diyalog 360

Türkçe sorulan rapor isteğini veritabanı şemasını bilerek SQL'e çeviren, sorguyu güvenle çalıştıran ve sonucu
grafik, tablo ve Türkçe özetle sunan sohbet tabanlı raporlama asistanı. Tamamen yerel çalışır (Qwen + llama.cpp).

## Çalıştırma

Sistem üç parçadan oluşur:

| Parça | Ne | Adres |
|---|---|---|
| Model sunucusu | `llama-server` (llama.cpp) + Qwen GGUF modeli | http://127.0.0.1:18080/v1 |
| API | .NET 10 (`api/`) | http://localhost:5080 |
| Önyüz | React + Vite (`web/`) | 5080 (derlenmiş) veya 5173 (geliştirme) |

Aşağıda önce iki işletim sisteminde ortak olan model dosyası, ardından **macOS** ve **Windows** için ayrı adımlar var.

### Model dosyası (ortak)

Varsayılan model: **Qwen3-30B-A3B-Instruct-2507 IQ4_XS** (~16 GB) —
[unsloth/Qwen3-30B-A3B-Instruct-2507-GGUF](https://huggingface.co/unsloth/Qwen3-30B-A3B-Instruct-2507-GGUF) reposundaki
`Qwen3-30B-A3B-Instruct-2507-IQ4_XS.gguf` dosyası
([doğrudan indirme](https://huggingface.co/unsloth/Qwen3-30B-A3B-Instruct-2507-GGUF/resolve/main/Qwen3-30B-A3B-Instruct-2507-IQ4_XS.gguf)).

| Donanım | Öneri |
|---|---|
| Apple Silicon, 24 GB+ birleşik bellek | Model tam GPU'da çalışır (`--n-gpu-layers 99`) |
| Windows + NVIDIA, 18 GB+ VRAM | Model tam GPU'da çalışır |
| Windows, daha az VRAM | `--n-gpu-layers` değerini düşürün (örn. 20) ya da `0` ile yalnızca CPU; model MoE (3B aktif) olduğu için CPU'da da kullanılabilir hızdadır, en az 32 GB RAM önerilir |
| Daha az bellek | Aynı repodaki daha küçük bir quant'ı seçin; doğruluğu `eval` ile ölçün |

---

## macOS

### Kurulum (bir kez)

```bash
brew install git dotnet node llama.cpp
git clone https://github.com/nevzatalkan/Dialog360.git
cd Dialog360
```

Model dosyasını indirin (örn. `~/models/` klasörüne). Scriptler modeli `LLM_MODEL` ortam değişkeninden okur:

```bash
export LLM_MODEL=~/models/Qwen3-30B-A3B-Instruct-2507-IQ4_XS.gguf
```

### Hızlı başlangıç (scriptlerle)

```bash
scripts/start-llm.sh        # 1. terminal: yerel model → http://127.0.0.1:18080/v1
scripts/demo.sh             # 2. terminal: önyüzü derler, her şeyi http://localhost:5080 adresinde sunar
```

Geliştirme için `scripts/dev.sh` (API 5080 + Vite 5173, sıcak yenileme).

### Manuel çalıştırma

**1. Model sunucusu (1. terminal)**

```bash
llama-server \
  --model ~/models/Qwen3-30B-A3B-Instruct-2507-IQ4_XS.gguf \
  --ctx-size 8192 --parallel 1 --n-gpu-layers 99 \
  --flash-attn on --cache-type-k q8_0 --cache-type-v q8_0 \
  --jinja --host 127.0.0.1 --port 18080
```

Log'da `server is listening on http://127.0.0.1:18080` görünce hazırdır. Kontrol: `curl 127.0.0.1:18080/health` → `{"status":"ok"}`

**2. API (2. terminal)**

```bash
cd api && dotnet run
```

- İlk açılışta `api/Data/bank.db` demo veritabanı (5.000 müşteri, ~157.000 kart işlemi) bugüne göre üretilir (~2 sn).
- Log'da `LLM ısındı ... önbelleğe alındı` satırı çıkana kadar ~20 sn bekleyin: sistem prompt'u modelin önbelleğine yüklenir.
- Kontrol: `curl localhost:5080/api/health` → `{"llm":true,...}`

**3. Önyüz**

A) Demo / tek adres — önyüz derlenip API'nin içinden sunulur:

```bash
cd web && npm install && npm run build
```

Ardından API'yi yeniden başlatın (`cd api && dotnet run`) ve **http://localhost:5080** adresini açın.

B) Geliştirme — 3. terminalde, kod değiştikçe sayfa kendiliğinden yenilenir:

```bash
cd web && npm install && npm run dev
```

**http://localhost:5173** adresini açın; `/api` istekleri 5080'deki API'ye yönlenir.

### Sorun giderme (macOS)

| Belirti | Çözüm |
|---|---|
| `couldn't bind HTTP server socket ... port: 18080` | Model sunucusu zaten çalışıyor; `lsof -i :18080` ile PID'i bulup `kill <PID>` ya da mevcut olanı kullanın |
| `address already in use` (5080) | Başka bir API örneği açık; `lsof -i :5080` ile bulup kapatın |
| Model belleğe sığmıyor (Metal OOM) | `--ctx-size` değerini düşürün ya da daha küçük bir quant kullanın |

---

## Windows

Komutlar **PowerShell** içindir (Windows Terminal önerilir). Windows PowerShell 5.1'de `&&` çalışmadığından komutlar `;` ile ayrılmıştır.

### Kurulum (bir kez)

```powershell
winget install Git.Git
winget install Microsoft.DotNet.SDK.10
winget install OpenJS.NodeJS.LTS
winget install llama.cpp
```

Kurulumdan sonra **terminali kapatıp yeniden açın** (PATH güncellensin). Kontrol: `dotnet --version`, `node --version`, `llama-server --version`.

> NVIDIA ekran kartında en iyi performans için llama.cpp'nin CUDA derlemesini
> [GitHub releases](https://github.com/ggml-org/llama.cpp/releases) sayfasından (`llama-*-bin-win-cuda-*.zip`) indirip
> `llama-server.exe`'nin bulunduğu klasörü PATH'e ekleyebilirsiniz.

```powershell
git clone https://github.com/nevzatalkan/Dialog360.git
cd Dialog360
```

Model dosyasını indirin, örneğin `C:\models\Qwen3-30B-A3B-Instruct-2507-IQ4_XS.gguf`.

### Hızlı başlangıç (scriptlerle)

```powershell
# 1. terminal: yerel model
powershell -ExecutionPolicy Bypass -File scripts\start-llm.ps1 -Model C:\models\Qwen3-30B-A3B-Instruct-2507-IQ4_XS.gguf

# 2. terminal: önyüzü derler, her şeyi http://localhost:5080 adresinde sunar
powershell -ExecutionPolicy Bypass -File scripts\demo.ps1
```

- `-ExecutionPolicy Bypass` yalnızca o komut için geçerlidir; sistem ayarını değiştirmez.
- Model yolunu her seferinde yazmamak için: `$env:LLM_MODEL = "C:\models\Qwen3-30B-A3B-Instruct-2507-IQ4_XS.gguf"`
- Ekran kartı belleği yetmiyorsa: `start-llm.ps1 ... -GpuLayers 20` (yalnızca CPU için `-GpuLayers 0`).
- Geliştirme için `scripts\dev.ps1` (API ayrı bir pencerede açılır, Vite 5173'te çalışır).

### Manuel çalıştırma

**1. Model sunucusu (1. terminal)**

```powershell
llama-server `
  --model C:\models\Qwen3-30B-A3B-Instruct-2507-IQ4_XS.gguf `
  --ctx-size 8192 --parallel 1 --n-gpu-layers 99 `
  --flash-attn on --cache-type-k q8_0 --cache-type-v q8_0 `
  --jinja --host 127.0.0.1 --port 18080
```

Kontrol (başka bir terminalde): `Invoke-RestMethod http://127.0.0.1:18080/health` → `status: ok`
(PowerShell'de `curl` farklı bir komuttur; gerçek curl için `curl.exe` yazın.)

**2. API (2. terminal)**

```powershell
cd api; dotnet run
```

Log'da `LLM ısındı ... önbelleğe alındı` satırını bekleyin. Kontrol: `Invoke-RestMethod http://localhost:5080/api/health`

**3. Önyüz**

A) Demo / tek adres:

```powershell
cd web; npm install; npm run build
```

Ardından API'yi yeniden başlatın (`cd api; dotnet run`) ve **http://localhost:5080** adresini açın.

B) Geliştirme (3. terminalde):

```powershell
cd web; npm install; npm run dev
```

**http://localhost:5173** adresini açın.

### Sorun giderme (Windows)

| Belirti | Çözüm |
|---|---|
| `... cannot be loaded because running scripts is disabled` | Scripti `powershell -ExecutionPolicy Bypass -File ...` ile çalıştırın |
| `llama-server` / `dotnet` / `npm` bulunamadı | Kurulumdan sonra terminali yeniden açın; olmazsa programın klasörünü PATH'e ekleyin |
| `couldn't bind HTTP server socket ... port: 18080` | Model sunucusu zaten çalışıyor: `netstat -ano \| findstr :18080` ile PID'i bulup `taskkill /PID <PID> /F` |
| `address already in use` (5080) | `netstat -ano \| findstr :5080` → `taskkill /PID <PID> /F` |
| Model yüklenirken bellek hatası / çok yavaş | `--n-gpu-layers` değerini düşürün, `--ctx-size 4096` deneyin ya da daha küçük bir quant kullanın |
| Windows Defender / güvenlik duvarı izni soruyor | Sunucular yalnızca `127.0.0.1`/`localhost` dinler; "Özel ağ" izni yeterlidir |

---

## Ortak

### Faydalı komutlar

Her iki işletim sisteminde aynıdır (Windows PowerShell 5.1'de `&&` yerine `;` kullanın):

```bash
cd api && dotnet run -- seed     # demo verisini bugüne göre yeniden üret (demo gününden önce çalıştırın)
cd api && dotnet run -- eval     # doğruluk ölçümü (model sunucusu açık olmalı), sonuç eval/results.json
dotnet test tests                # güvenlik testleri
```

### Ortak sorunlar

| Belirti | Çözüm |
|---|---|
| Arayüzde "Model kapalı · yalnızca önbellek" | Model sunucusu çalışmıyor; daha önce sorulmuş sorular önbellekten cevaplanmaya devam eder |
| "Geçen ay" gibi sorular boş dönüyor | Veri eski tarihli; `dotnet run -- seed` (api klasöründe) |
| İlk soru çok yavaş | API açılışındaki ısınma (~20 sn, Windows'ta CPU modunda daha uzun) bitmeden soru sorulmuştur; bir kez bekleyin |

## Akış

```
Soru ──► Önbellek (aynı soru + aynı bağlam) ──► Yerel LLM (JSON şemalı çıktı: rapor | netlestirme | ret)
                                                   │
             ┌─────────────── hata mesajı (en fazla 2 düzeltme) ◄──┐
             ▼                                                     │
        SqlGuard (SQLite authorizer) ──► QueryRunner (read-only, 15 sn, 1000 satır) ──► Grafik kuralı + Türkçe özet (kodda)
```

- **LLM yalnızca SQL önerir**; neyin çalışacağına deterministik kod karar verir.
- **SqlLint** (`Services/SqlLint.cs`): iş kuralına aykırı bilinen kalıpları (örn. "son 6 ay" için kayan pencere, "aynı dönem" yerine tüm geçen yıl) yakalar ve düzeltme döngüsüne geri verir.
- **Sabit prompt öneki** (kurallar + şema + sözlük + örnekler, ~4.800 token) açılışta modele bir kez okutulur;
  sonraki sorularda llama-server KV önbelleğinden gelir, yalnızca soru ve sohbet geçmişi işlenir.
- **Özet ve grafik seçimi LLM'siz**: sayılar kodda hesaplanır, model sayı uyduramaz.
- **Takip soruları**: son 6 mesaj (önceki SQL'ler dahil) prompt'a eklenir; "bunu şubeye göre kır" önceki SQL'i değiştirir.

## Giriş

Uygulama açılışta giriş ekranı gösterir. Tek kullanıcı vardır: **kullanıcı adı `admin`, şifre `admin`**.

- Kullanıcı `app.db` içindeki `AppUser` tablosunda tutulur; tablo boşsa ilk açılışta oluşturulur. Şifre düz metin değil, PBKDF2-SHA256 (100.000 tur, tuzlu) hash olarak saklanır.
- Rapor sorguları yalnızca `bank.db` üzerinde çalışır. `AppUser` tablosu şema kataloğunda yoktur, LLM'e gösterilmez; SQL panelinden `SELECT * FROM AppUser` yazılsa bile "no such table" hatası döner.
- Oturum HttpOnly + SameSite=Strict çerezle tutulur (8 saat, kullandıkça uzar). `/api/chat`, `/api/sql`, `/api/history`, `/api/schema` girişsiz 401 döner.
- `dotnet run -- eval` HTTP katmanını kullanmadığı için girişten etkilenmez.

## Sohbet geçmişi

Sol üstteki menü düğmesi (☰) geçmiş panelini açar/kapatır; masaüstünde tercih hatırlanır, mobilde panel çekmece olarak açılır.

- Sohbetler son etkinliğe göre yeniden eskiye sıralanır ve Bugün, Dün, Son 7 gün, Son 30 gün ve ay ay gruplanır. Her satırda başlık, saat/tarih, soru ve rapor sayısı görünür.
- Bir sohbete tıklanınca tüm mesajlar o anki haliyle açılır: her rapor kartı SQL'i, satırları, grafiği, özeti, varsayımları, süreleri ve kendini düzeltme geçmişiyle birlikte `app.db`'de saklıdır. Mesajların üstünde gönderilme zamanı yazar. Açılan sohbette takip sorusu sorulabilir.
- Arama konuşmanın tamamında yapılır: sorular, rapor başlığı ve özeti, SQL, açıklama, varsayımlar, kolon adları, tablo değerleri (rapor başına ilk 300 satır), netleştirme soruları ve ret gerekçeleri. Büyük/küçük harf ve Türkçe karakter duyarsızdır ("kadikoy" = "KADIKÖY" = "Kadıköy"); her sonucun altında eşleşmenin geçtiği alıntı vurgulanır. Bunun için her mesajın düz arama metni `Message.SearchText`'te tutulur (JSON alan adları aranmaz); eski mesajlar ilk açılışta doldurulur.
- Sohbetler yeniden adlandırılabilir ve silinebilir; silme denetim logunu (`QueryLog`) etkilemez.
- Başlık, sohbetin ilk sorusudur (elle SQL ile başlayan sohbetlerde "SQL: ..." biçiminde); yeniden adlandırılırsa o başlık kullanılır.
- Eval'in oluşturduğu sohbetler ve boş sohbetler listede görünmez. Sohbetler giriş yapan kullanıcıya bağlanır; başka bir kullanıcının sohbetine erişilemez.
- Uçlar: `GET /api/chats?q=`, `PATCH /api/chats/{id}`, `DELETE /api/chats/{id}`, `GET /api/history/{id}` (hepsi giriş gerektirir).

## ZB ile kriter seçme

Soru kutusuna **ZB** yazıp bir ifade ekleyin (örn. `ZB yaş`, `ZB genç kadın`, `ZB kart limiti`, `ZB Platinum`).

- "ZB " yazılınca ZB kalın ve çerçeveli bir rozete dönüşür; ardından gelen ifade altı noktalı çizgiyle vurgulanır.
- Yazmaya ara verince (ya da Enter'a veya rozete basınca) ifade dil modeliyle analiz edilir ve soru kutusunun üstünde bir **kriter penceresi** açılır:
  - **Ana kriter:** ifadeye karşılık gelen alan (yaş için hazır aralıklar ve en az / en çok kutuları).
  - **Ek filtreler:** modelin önerdiği alanlar (cinsiyet, segment, şehir…). "Filtre ekle" ile diğer alanlar da eklenebilir: bölge, şube, kart tipi, kart limiti, TL mevduat bakiyesi, hesap türü, ürün, harcama sektörü, işlem kanalı, müşteri olma süresi, işlem dönemi.
  - İfade bir değer içeriyorsa model başlangıç değerlerini doldurur: "genç kadın" için yaş 18-30 ve cinsiyet Kadın seçili gelir.
  - Altta "Soruya eklenecek" cümlesi canlı olarak görünür.
- **Tamam** deyince "ZB ifade" bölümü kriter cümlesiyle değiştirilir: *"Yaşı 26-35 arası, cinsiyeti Kadın, segmenti Premium olan, Market sektöründe kart harcaması yapan müşteriler"*. Cümle soru kutusunda renkli gösterilir; ardından yazmaya devam edip **Sor** dersiniz ve soru normal akışla rapora dönüşür.
- **Vazgeç**/Esc pencereyi kapatır; ZB rozetine tıklayınca yeniden açılır. Netleştirilmemiş bir ZB ile Enter'a basılırsa önce pencere açılır; ikinci kez basılırsa ZB işareti atılıp soru olduğu gibi gönderilir.
- **Bağlama göre daraltma:** Pencere, sorudaki mevcut kriterleri dikkate alır. Önce `ZB şehir` ile Ankara seçip sonra `ZB ilçe` yazınca ilçe/şube listesinde yalnızca Ankara şubeleri (Kızılay, Çankaya) gelir. Pencerenin üstünde "Sorudaki kriterler: Şehir: Ankara" etiketi görünür.
  - Bağlam iki kaynaktan gelir: önceki ZB pencerelerinde yapılan seçimler ve soruda yazılı değerler (örn. "Ankara'daki", "Premium", "kadın").
  - Aynı pencerede seçim değiştikçe diğer alanlar da anında daralır: Şehir'de İstanbul seçilince Şube listesi İstanbul şubelerine iner.
  - Her seçeneğin yanında o bağlamdaki müşteri sayısı, altta "Bu kriterlere uyan N müşteri" yazar; müşterisi olmayan değerler listelenmez. Aralıkların (yaş, limit…) sınırları da bağlama göre hesaplanır.
  - Arka arkaya eklenen kriterler tek cümlede birleşir: *"Şehri Ankara, şubesi Çankaya olan müşteriler"*.
  - API: `POST /api/zb/facets {context}`.
- Model kapalıysa pencere yine açılır: alanlar kelime eşleştirmesiyle seçilir ("Kural tabanlı" etiketi).
- Alan listesi ve değerler (yaş aralığı, şehirler, sektörler, limit aralığı) açılışta veritabanından okunur (`Services/ZbFilters.cs`); yeni alan eklemek tek satırdır. API: `POST /api/zb/analyze {term, text}`.
- Bu özellik için `Musteri` tablosuna **Cinsiyet** kolonu eklendi. Eski `bank.db` dosyalarında kolon açılışta otomatik eklenir ve müşterinin adından doldurulur; diğer veriler değişmez.

## Detaya inme (drill down / up)

Rapordaki bir değere tıklayarak detaya inilir; takip sorusu yazmaya gerek kalmaz.

- **Tıklama:** grafikte bir çubuğa, halka dilimine veya çizgi noktasına; tabloda altı çizili değerlere (Bölge, Şehir, Şube, Ay, Sektör…) tıklayın. Açılan menüde iki grup vardır:
  - **Detaya in:** aynı hiyerarşide alt seviyeler — Bölge → Şehir → Şube → Müşteri, Yıl → Ay → Gün.
  - **Farklı kırılım:** başka bir boyut — Sektör, Kanal, Segment, Kart tipi, Hesap türü, Ay. Yalnızca sorgunun olgu tablosuna satırları çoğaltmadan bağlanabilen boyutlar sunulur; örneğin mevduat raporunda sektör ve kanal çıkmaz.
- **Geri dönme (drill up):** detay raporunun üstünde yol çubuğu görünür: *Bölgelere Göre TL Mevduat › Marmara › İstanbul*. Önceki bir adıma tıklayınca o rapor kayıttan anında geri gelir; model ya da veritabanı yeniden çalışmaz. **↑ Bölgelere çık** düğmesi raporu bir üst seviyede yeniden hesaplar ve alt seviye filtrelerini kaldırır.
- **Nasıl çalışır:**
  - Basit sorgularda (tek SELECT + GROUP BY) önceki SQL **dil modeli kullanılmadan** yeniden yazılır (`Services/Drill.cs`): tıklanan değer WHERE'e eklenir, kırılım kolonu hedef seviyeyle değiştirilir, eksik tablolar yalnızca çoka-bir JOIN'lerle eklenir. Sorgu yine SqlGuard'dan geçer. Rapor kartında "detay · LLM'siz" rozeti görünür.
  - CTE, pencere fonksiyonu veya türetilmiş tablo içeren sorgularda istek, "diğer koşulları aynen koru" talimatıyla modele takip mesajı olarak gider.
- **Kayıt:** detay adımları geçmişe yazılır ("Marmara → Şehirlere in"); sohbet yeniden açıldığında yol çubuğu ve tıklama özellikleri geri gelir.
- **API:** `POST /api/drill {chatId, messageId, column, value, target}` (value `null` → üst seviyeye çık), `POST /api/drill/back {chatId, messageId}`.
- **Seviyeleri değiştirmek:** `DrillPlanner.Levels` listesine yeni bir seviye (tablo, ifade, takma ad) eklemek yeterlidir.

## Güvenlik katmanları

| Katman | Ne yapar |
|---|---|
| Bağlantı | Rapor veritabanı `Mode=ReadOnly` ile açılır |
| SqlGuard | SQLite'ın kendi parser'ı sorguyu derlerken her erişim için authorizer'ı çağırır: yalnızca SELECT, şemadaki tablolar ve izinli fonksiyonlar geçer; INSERT/UPDATE/DELETE/DROP/PRAGMA/ATTACH reddedilir; tek ifade ve `sqlite3_stmt_readonly` kontrol edilir |
| KVKK (isteğe bağlı) | `appsettings.json` → `Privacy:ProtectPersonalData` (varsayılan `false`). `true` yapıldığında: kişisel veri isteyen sorular LLM'e gitmeden durdurulur, PII kolonları (`schema.notes.json`'da `"pii": true`) SQL doğrulayıcıda engellenir ve `Prompt/kvkk.md` kuralları prompt'a eklenir. Her iki durumda da LLM'e satır verisi gönderilmez |
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
scripts/    start-llm, dev, demo (.sh = macOS/Linux, .ps1 = Windows)
```

LLM istemcisi OpenAI uyumludur; `appsettings.json` → `Llm:BaseUrl/Model/ApiKey` değiştirilerek Azure OpenAI vb. bir servise geçilebilir
(llama'ya özel `LlamaCachePrompt` ayarını `false` yapın).
