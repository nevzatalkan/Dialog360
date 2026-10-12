# Yerel Qwen modelini OpenAI uyumlu uçla (http://127.0.0.1:18080/v1) başlatır. (Windows / PowerShell)
# Kullanım:  powershell -ExecutionPolicy Bypass -File scripts\start-llm.ps1 [-Model C:\models\x.gguf] [-GpuLayers 99]
param(
    [string]$Model = $(if ($env:LLM_MODEL) { $env:LLM_MODEL } else { Join-Path $HOME 'models\Qwen3-30B-A3B-Instruct-2507-IQ4_XS.gguf' }),
    [int]$Port = $(if ($env:LLM_PORT) { [int]$env:LLM_PORT } else { 18080 }),
    # Ekran kartı belleği modele yetmiyorsa düşürün (örn. 20); 0 = yalnızca CPU
    [int]$GpuLayers = 99
)

try {
    Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 2 | Out-Null
    Write-Host "llama-server zaten 127.0.0.1:$Port üzerinde çalışıyor; yeniden başlatmaya gerek yok."
    exit 0
} catch { }

if (-not (Test-Path $Model)) {
    Write-Error "Model dosyası bulunamadı: $Model`n-Model parametresiyle ya da LLM_MODEL ortam değişkeniyle yolu verin."
    exit 1
}
if (-not (Get-Command llama-server -ErrorAction SilentlyContinue)) {
    Write-Error "llama-server bulunamadı. Kurulum: winget install llama.cpp (sonra terminali yeniden açın)"
    exit 1
}

& llama-server --model $Model `
    --ctx-size 8192 --parallel 1 --n-gpu-layers $GpuLayers `
    --flash-attn on --cache-type-k q8_0 --cache-type-v q8_0 `
    --jinja --host 127.0.0.1 --port $Port
exit $LASTEXITCODE
