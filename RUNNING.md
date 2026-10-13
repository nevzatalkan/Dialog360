# Dialog360 Local Çalıştırma Kılavuzu

Bu dosya, projeyi yerelde çalıştırmak için gerekli adımları özetler.

## Gereksinimler

- Git
- .NET 10 SDK
- Node.js LTS
- Ollama

## 1) Gerekli araçları kurun

PowerShell ile:

```powershell
winget install Git.Git
winget install Microsoft.DotNet.SDK.10
winget install OpenJS.NodeJS.LTS
```

Ollama'yı kurduktan sonra terminali kapatıp yeniden açın.

## 2) Modeli indirin

```powershell
ollama pull qwen2.5-coder:7b
```

Varsayılan ayarlar projede Ollama yerel uç noktasını kullanır:

```text
http://127.0.0.1:11434/v1
```

## 3) API'yi çalıştırın

```powershell
cd C:\WorkSpace\AI\Dialog360\api
dotnet run
```

API hazır olduğunda şu adresi kontrol edebilirsiniz:

```powershell
Invoke-RestMethod http://localhost:5080/api/health
```

`llm: true` çıktısı görünmesi beklenir.

## 4) Frontend'i çalıştırın

Ayrı bir terminal açın:

```powershell
cd C:\WorkSpace\AI\Dialog360\web
npm install
npm run dev
```

Tarayıcıda şu adresi açın:

```text
http://localhost:5173
```

## 5) Demo / tek adres modu

Web tarafını build edip tek portta çalıştırmak isterseniz:

```powershell
cd C:\WorkSpace\AI\Dialog360\web
npm install
npm run build
```

Sonra API'yi yeniden başlatın ve tarayıcıda:

```text
http://localhost:5080
```

adresini açın.

## 6) Alternatif: llama.cpp kullanımı

README'de anlatılan llama.cpp alternatifi için:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-llm.ps1 -Model C:\models\Qwen3-30B-A3B-Instruct-2507-IQ4_XS.gguf
powershell -ExecutionPolicy Bypass -File .\scripts\demo.ps1
```

Ayrıca model yolunu sabit tutmak isterseniz:

```powershell
$env:LLM_MODEL = "C:\models\Qwen3-30B-A3B-Instruct-2507-IQ4_XS.gguf"
```

## 7) Hızlı başlangıç özeti

```powershell
ollama pull qwen2.5-coder:7b
cd C:\WorkSpace\AI\Dialog360\api
dotnet run
```

Ayrı terminal:

```powershell
cd C:\WorkSpace\AI\Dialog360\web
npm install
npm run dev
```

## Sorun giderme

### Port kullanım hatası

```powershell
netstat -ano | findstr :5080
netstat -ano | findstr :18080
```

Sonra ilgili PID'yi kapatın:

```powershell
taskkill /PID <PID> /F
```

### Script çalıştırma engeli

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\demo.ps1
```

### `dotnet`, `node` veya `ollama` komutunu bulamıyorsa

Terminali yeniden açın. Hala yoksa PATH ortam değişkenini kontrol edin.

## Son kontrol

- Ollama çalışıyor mu?
- API `http://localhost:5080/api/health` yanıt veriyor mu?
- Frontend `http://localhost:5173` açılıyor mu?

İsterseniz bir sonraki adım olarak bu dosyayı daha ayrıntılı bir geliştirici kurulum rehberine de dönüştürebilirim.
