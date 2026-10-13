# Yerel ses tanıma (whisper.cpp) → http://127.0.0.1:18090  (Windows / PowerShell)
# Kullanım:  powershell -ExecutionPolicy Bypass -File scripts\start-whisper.ps1 [-Model C:\models\ggml-large-v3-turbo-q5_0.bin]
param(
    [string]$Model = $(if ($env:WHISPER_MODEL) { $env:WHISPER_MODEL } else { Join-Path $HOME 'models\ggml-large-v3-turbo-q5_0.bin' }),
    [int]$Port = $(if ($env:WHISPER_PORT) { [int]$env:WHISPER_PORT } else { 18090 })
)

try {
    Invoke-WebRequest -Uri "http://127.0.0.1:$Port/" -UseBasicParsing -TimeoutSec 2 | Out-Null
    Write-Host "whisper-server zaten 127.0.0.1:$Port üzerinde çalışıyor; yeniden başlatmaya gerek yok."
    exit 0
} catch { }

if (-not (Test-Path $Model)) {
    Write-Error "Whisper modeli bulunamadı: $Model`nİndirin: https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin"
    exit 1
}
$server = Get-Command whisper-server -ErrorAction SilentlyContinue
if (-not $server) { $server = Get-Command whisper-server.exe -ErrorAction SilentlyContinue }
if (-not $server) {
    Write-Error "whisper-server bulunamadı. whisper.cpp Windows derlemesini https://github.com/ggml-org/whisper.cpp/releases adresinden indirip klasörünü PATH'e ekleyin."
    exit 1
}

& $server.Source -m $Model --host 127.0.0.1 --port $Port
exit $LASTEXITCODE
