# Diyalog 360

Türkçe sorulan rapor isteğini veritabanı şemasını bilerek SQL'e çeviren, sorguyu güvenle çalıştıran ve sonucu
grafik, tablo ve Türkçe özetle sunan sohbet tabanlı raporlama asistanı. Tamamen yerel çalışır (Qwen + llama.cpp).

Ayrıntılı mimari: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) · Sıfırdan üretim prompt'u: [docs/REBUILD_PROMPT.md](docs/REBUILD_PROMPT.md)

## Çalıştırma

Sistem üç parçadan oluşur:

| Parça | Ne | Adres |
|---|---|---|
| Model sunucusu | `llama-server` (llama.cpp) + Qwen GGUF modeli | http://127.0.0.1:18080/v1 |
| API | .NET 10 (`api/`) | http://localhost:5080 |
| Önyüz | React + Vite (`web/`) | 5080 (derlenmiş) veya 5173 (geliştirme) |

Aşağıda önce iki işletim sisteminde ortak olan model dosyası, ardından **macOS** ve **Windows** için ayrı adımlar var.

## Azure'a deploy (Microsoft Foundry + App Service + Key Vault)

```bash
az login
scripts/deploy-azure.sh
```

Script tek bir kaynak grubunda (`rg-diyalog360`, Sweden Central) şunları oluşturur ve uygulamayı deploy eder; ikinci çalıştırmada
yalnızca kodu günceller:

| Kaynak | Görev |
|---|---|
| Microsoft Foundry kaynağı + `diyalog360` projesi + `gpt-4.1-mini` deployment (GlobalStandard) | LLM (`https://foundry-diyalog360-<sonek>.services.ai.azure.com/openai/v1`); token başına ücret; izleme ve değerlendirme Foundry portalında |
| App Service planı B1 Linux + web app (.NET 10) | Backend **ve** önyüz aynı adreste (`https://diyalog360-api-<sonek>.azurewebsites.net`); ~13 USD/ay |
| Key Vault (RBAC) | Model anahtarı ve giriş şifre özeti; web app **yönetilen kimlikle** okur, App Service ayarlarında yalnızca Key Vault referansı durur |

- İlk çalıştırmada giriş kullanıcısı sorulur; Azure'a yalnızca PBKDF2 özeti gider (şifrenin kendisi değil).
- Veriler (`bank.db`, `app.db`, oturum anahtarları) deploy'larda silinmeyen `/home/data` klasöründedir; yayın paketine `Data/` asla girmez
  (script bunu kontrol eder).
- Bulutta sesle soru kapalıdır (`Speech:Provider=None`); Azure AI Speech eklenebilir.
- Foundry modeliyle eval: **24/24**, soru başına ~2–7 sn. Ölçmek için:
  `Llm__BaseUrl=https://<foundry>.services.ai.azure.com/openai/v1 Llm__Model=gpt-4.1-mini Llm__ApiKey=... Llm__LlamaCachePrompt=false dotnet run -- eval`
- Kaldırmak için: `az group delete -n rg-diyalog360` (Key Vault 7 gün geri dönüşüm kutusunda kalır).

### Otomatik deploy (GitHub Actions)

`main`'e her push'ta `.github/workflows/deploy-azure.yml` önyüzü derler, testleri çalıştırır ve geçerse paketi App Service'e yükler
(Azure kaynaklarına ve ayarlarına dokunmaz). Tek seferlik kurulum, `deploy-azure.sh`'den sonra:

1. Azure Cloud Shell'de (ya da `az login` yapılmış terminalde): `scripts/setup-github-deploy.sh`. GitHub'a yalnızca bu repo ve
   `production` ortamı için şifresiz (OIDC) giriş ve yalnızca bu web app'e "Website Contributor" yetkisi verir; sonda 4 değer yazdırır.
2. GitHub → Settings → Environments → **New environment** `production` → yazdırılan 4 değeri **Variables** olarak ekleyin
   (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_WEBAPP_NAME`; hiçbiri gizli değildir).
3. Actions → **Azure'a deploy** → *Run workflow*.

Değişkenler tanımlanana kadar workflow build ve testleri yine çalıştırır, deploy adımını uyarıyla atlar.

**Güvenlik kapısı:** deploy ancak şu kontrollerin hepsi temizse çalışır; tek bulgu deploy'u durdurur.

| Kontrol | Araç | Bulgu olursa |
|---|---|---|
| Kod taraması (C#, TypeScript) | GitHub CodeQL, `security-extended` sorguları | Security → Code scanning'de görünür; düzeltin ya da yanlış alarmsa gerekçesiyle *Dismiss* edin |
| Bağımlılık açıkları | `npm audit` (her önem düzeyi), `dotnet list package --vulnerable --include-transitive` | Paketi güncelleyin |
| Gizli anahtar | gitleaks (tüm git geçmişi) | Anahtarı iptal edip yenileyin; yanlış alarmsa satıra `gitleaks:allow` ya da `.gitleaksignore`'a parmak izi |

Repo ayarlarında **Secret scanning** ve **Push protection** da açık olmalı (Settings → Code security); böylece anahtar içeren push daha main'e ulaşmadan engellenir.

## GitHub Codespaces (kurulumsuz, ücretsiz LLM)

Bilgisayara hiçbir şey kurmadan, tarayıcıda çalıştırmak için: GitHub'da repo sayfası → **Code → Codespaces →
Create codespace on main**. Ortam (.NET 10 + Node 22) hazırlanır, `scripts/codespace.sh` kendiliğinden başlar ve
5080 portu tarayıcıda açılır. İlk açılışta terminal, giriş ekranı için kullanıcı adı ve şifre sorar.

- Yerel Qwen yerine ücretsiz **[GitHub Models](https://github.com/marketplace/models)** API'si kullanılır
  (varsayılan `openai/gpt-4.1-mini`). Anahtar gerekmez: Codespaces'in verdiği `GITHUB_TOKEN` kullanılır.
- Ücretsiz katmanın dakikalık/günlük istek sınırı vardır; aynı soru tekrar sorulduğunda önbellekten gelir.
- Token "unauthorized/forbidden" hatası verirse: *Models: read* izinli bir fine-grained token oluşturun ve
  GitHub → Settings → Codespaces → Secrets altına `GH_MODELS_TOKEN` adıyla ekleyin.
- Başkasıyla paylaşmak için VS Code'un **Ports** sekmesinde 5080'e sağ tık → *Port Visibility → Public*; uygulama girişli olduğu için kullanıcı adı ve şifreyi de paylaşmanız gerekir.
- Codespace boşta kalınca durur; yeniden açınca `scripts/codespace.sh` tekrar çalışır.

Başka bir OpenAI uyumlu ücretsiz servis (Google Gemini, Groq, OpenRouter...) için scripti ortam değişkenleriyle
çalıştırın (servis JSON şemalı çıktıyı desteklemelidir; doğruluğu `dotnet run -- eval` ile ölçün):

```bash
Llm__BaseUrl=https://generativelanguage.googleapis.com/v1beta/openai \
Llm__Model=gemini-2.5-flash Llm__ApiKey=$GEMINI_API_KEY scripts/codespace.sh
```

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

**Sesle soru (opsiyonel):** Whisper modeli **ggml-large-v3-turbo-q5_0.bin** (~574 MB) —
[ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp)
([doğrudan indirme](https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin)).
Qwen ile birlikte yaklaşık 1 GB ek bellek kullanır (24 GB'lık M4'te ikisi birlikte çalışır). Kurulmazsa mikrofon butonu pasif görünür,
uygulamanın geri kalanı etkilenmez. Yerel Whisper yerine Azure AI Speech de kullanılabilir (bkz. [Sesle soru](#sesle-soru)).

---

## macOS

### Kurulum (bir kez)

```bash
brew install git dotnet node llama.cpp whisper-cpp
git clone https://github.com/nevzatalkan/Dialog360.git
cd Dialog360
```

Model dosyasını indirin (örn. `~/models/` klasörüne). Scriptler modeli `LLM_MODEL` ortam değişkeninden okur:

```bash
export LLM_MODEL=~/models/Qwen3-30B-A3B-Instruct-2507-IQ4_XS.gguf
```

Uygulamaya giriş yapacak **tek kullanıcıyı** oluşturun (kullanıcı adı ve şifre sorulur; şifre en az 8 karakter):

```bash
cd api && dotnet run -- set-password
```

### Hızlı başlangıç (scriptlerle)

```bash
scripts/start-llm.sh        # 1. terminal: yerel model → http://127.0.0.1:18080/v1
scripts/start-whisper.sh    # 2. terminal (opsiyonel): sesle soru → http://127.0.0.1:18090
scripts/demo.sh             # 3. terminal: önyüzü derler, her şeyi http://localhost:5080 adresinde sunar
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

**1b. Ses tanıma (opsiyonel, ayrı terminal)**

```bash
whisper-server -m ~/models/ggml-large-v3-turbo-q5_0.bin --host 127.0.0.1 --port 18090
```

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

Sesle soru için (opsiyonel): whisper.cpp'nin Windows derlemesini [GitHub releases](https://github.com/ggml-org/whisper.cpp/releases)
sayfasından indirip `whisper-server.exe`'nin bulunduğu klasörü PATH'e ekleyin. Ses tarayıcıda WAV'a çevrildiği için ffmpeg gerekmez.

Kurulumdan sonra **terminali kapatıp yeniden açın** (PATH güncellensin). Kontrol: `dotnet --version`, `node --version`, `llama-server --version`.

> NVIDIA ekran kartında en iyi performans için llama.cpp'nin CUDA derlemesini
> [GitHub releases](https://github.com/ggml-org/llama.cpp/releases) sayfasından (`llama-*-bin-win-cuda-*.zip`) indirip
> `llama-server.exe`'nin bulunduğu klasörü PATH'e ekleyebilirsiniz.

```powershell
git clone https://github.com/nevzatalkan/Dialog360.git
cd Dialog360
```

Model dosyasını indirin, örneğin `C:\models\Qwen3-30B-A3B-Instruct-2507-IQ4_XS.gguf`.

Uygulamaya giriş yapacak **tek kullanıcıyı** oluşturun (kullanıcı adı ve şifre sorulur; şifre en az 8 karakter):

```powershell
cd api; dotnet run -- set-password; cd ..
```

### Hızlı başlangıç (scriptlerle)

```powershell
# 1. terminal: yerel model
powershell -ExecutionPolicy Bypass -File scripts\start-llm.ps1 -Model C:\models\Qwen3-30B-A3B-Instruct-2507-IQ4_XS.gguf

# 2. terminal (opsiyonel): sesle soru
powershell -ExecutionPolicy Bypass -File scripts\start-whisper.ps1 -Model C:\models\ggml-large-v3-turbo-q5_0.bin

# 3. terminal: önyüzü derler, her şeyi http://localhost:5080 adresinde sunar
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
cd api && dotnet run -- set-password   # tek kullanıcıyı oluştur / şifre değiştir
cd api && dotnet run -- seed     # demo verisini bugüne göre yeniden üret (demo gününden önce çalıştırın)
cd api && dotnet run -- eval     # doğruluk ölçümü (model sunucusu açık olmalı), sonuç eval/results.json
dotnet test tests                # güvenlik testleri
```

### Ortak sorunlar

| Belirti | Çözüm |
|---|---|
| Giriş ekranında "Henüz kullanıcı tanımlı değil" | `dotnet run -- set-password` (api klasöründe) |
| Şifreyi unuttum / değiştirmek istiyorum | Aynı komutu yeniden çalıştırın; açık oturumların hepsi kapanır |
| "Çok fazla hatalı deneme" | 5 hatalı denemeden sonra giriş 5 dakika kilitlenir; bekleyin |
| Mikrofon butonu soluk / tıklanmıyor | Ses tanıma sunucusu kapalı: `scripts/start-whisper` ile başlatın ya da `Speech:Provider` ayarını kontrol edin |
| "Mikrofona erişilemedi" | Tarayıcının mikrofon iznini verin; sayfa `localhost` ya da HTTPS üzerinden açılmalı |
| Arayüzde "Model kapalı · yalnızca önbellek" | Model sunucusu çalışmıyor; daha önce sorulmuş sorular önbellekten cevaplanmaya devam eder |
| "Geçen ay" gibi sorular boş dönüyor | Veri eski tarihli; `dotnet run -- seed` (api klasöründe) |
| İlk soru çok yavaş | API açılışındaki ısınma (~20 sn, Windows'ta CPU modunda daha uzun) bitmeden soru sorulmuştur; bir kez bekleyin |

## Kullanım

- **Giriş:** Uygulama tek kullanıcılıdır; `set-password` ile tanımlanan kullanıcı adı ve şifreyle girilir. Sağ üstteki **Çıkış** oturumu sunucuda da sonlandırır.
- **Sesle soru:** Soru kutusunun sağındaki 🎤 butonuna basıp konuşun, bitince tekrar basın (en fazla 30 sn). Metin kutuya
  yazılır; kontrol edip **Sor**'a basın. Ses tanıma hatalarını ("yadeleri" → iade gibi) dil modeli düzeltip varsayımlarda belirtir.
- **Geçmiş Sorgularım:** Sol üstteki ☰ menüsü önceki konuşmaları listeler. Bir konuşmaya tıklayınca raporlarıyla birlikte açılır ve
  kaldığı yerden takip sorusu sorulabilir. Çöp kutusu konuşmayı (onaydan sonra) hem bu tarayıcıdan hem sunucudan siler.
  Konuşma listesi tarayıcının `localStorage`'ında tutulur.

## Sesle soru

```
Mikrofon ─► tarayıcıda 16 kHz mono WAV ─► POST /api/speech (giriş + X-Diyalog360 başlığı)
        ─► Speech:Provider ─┬─ Whisper (yerel whisper.cpp, :18090) ─► metin ─► soru kutusu
                            └─ AzureSpeech (Azure AI Speech, kısa ses REST)
```

- Ses tarayıcıda WAV'a çevrilir: sunucuda ffmpeg gerekmez, aynı dosya her iki sağlayıcıya gider.
- Whisper'a **alan terimleri** (şube, şehir, sektör, segment, kart tipi... veri kataloğundan otomatik) ön metin olarak verilir; böylece
  "Alsancak", "KOBİ", "Karşıyaka" doğru yazılır. Üretilen metin `api/Data/speech-prompt.generated.txt` dosyasına yazılır.
- Azure'a geçmek için `appsettings.json` → `Speech: { "Provider": "AzureSpeech", "AzureRegion": "westeurope", "AzureKey": "..." }`
  (anahtarı user-secrets/ortam değişkeniyle verin: `Speech__AzureKey`). Kapatmak için `"Provider": "None"`.
- Ölçüm (M4, Qwen ile aynı anda): 3,5–4,6 sn'lik konuşma ~1,1–1,6 sn'de yazıya dönüyor.

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
| Giriş | Tek kullanıcı; şifre PBKDF2-SHA256 (600.000 tur) ile özetlenip `api/Data/auth.json`'da saklanır (repoya girmez). 5 hatalı denemede 5 dk kilit, giriş ucunda dakikada 10 istek sınırı |
| Oturum | Şifreli `HttpOnly` + `SameSite=Strict` çerez, 8 saat (kayan). Oturum sunucuda da kayıtlıdır: çıkışta ve şifre değişince geçersizleşir. Tüm `/api` uçları girişsiz 401 döner |
| HTTP başlıkları | CSP (`default-src 'self'`), `X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy: no-referrer`, API'de `Cache-Control: no-store` |
| Bağlantı | Rapor veritabanı `Mode=ReadOnly` ile açılır |
| SqlGuard | SQLite'ın kendi parser'ı sorguyu derlerken her erişim için authorizer'ı çağırır: yalnızca SELECT, şemadaki tablolar ve izinli fonksiyonlar geçer; INSERT/UPDATE/DELETE/DROP/PRAGMA/ATTACH reddedilir; tek ifade ve `sqlite3_stmt_readonly` kontrol edilir |
| KVKK | PII kolonları (`schema.notes.json`'da `"pii": true`) SELECT, WHERE, JOIN dahil hiçbir yerde okunamaz; LLM'e satır verisi gönderilmez |
| Çalıştırma | 15 sn zaman aşımı (progress handler), 1000 satır limiti, 1 MB metin/değer sınırı, sorgu planında index'siz büyük tablo taraması uyarısı |
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
cd api && dotnet run -- eval        # tüm eval seti (önbelleksiz), sonuç eval/results.json — son koşu: 24/24
cd api && dotnet run -- eval q18    # tek soru
dotnet test tests                   # 38 test: SqlGuard, giriş, ses + örnek SQL'lerin geçerliliği
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
  Services/ SqlGuard, QueryRunner, SchemaCatalog, PromptBuilder, LlmClient, ChatOrchestrator, ReportBuilder,
            AuthService, SpeechToText, AppStore, EvalRunner
  Data/     DemoSeeder + bank.db (rapor verisi) + app.db (sohbet, önbellek, oturum, log) + auth.json (kullanıcı)
  Prompt/   LLM'e giden metinler
web/        React + TypeScript + Vite + Tailwind + Recharts (giriş, sohbet, rapor kartı, Geçmiş Sorgularım, mikrofon)
eval/       Doğruluk ölçüm seti (24 soru; son koşu 24/24)
tests/      xUnit testleri (38: SQL doğrulayıcı, giriş, ses)
docs/       ARCHITECTURE.md (mimari), REBUILD_PROMPT.md (sıfırdan üretim prompt'u)
.devcontainer/  GitHub Codespaces ortamı
scripts/    start-llm, start-whisper, dev, demo (.sh = macOS/Linux, .ps1 = Windows), codespace (Codespaces + GitHub Models),
            deploy-azure.sh (Microsoft Foundry + App Service + Key Vault)
```

LLM istemcisi OpenAI uyumludur; `appsettings.json` → `Llm:BaseUrl/Model/ApiKey` değiştirilerek Azure OpenAI vb. bir servise geçilebilir
(llama'ya özel `LlamaCachePrompt` ayarını `false` yapın; servisin `/models` ucu yoksa `Llm:HealthUrl` verin).
