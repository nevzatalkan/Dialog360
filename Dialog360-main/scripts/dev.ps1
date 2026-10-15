# Geliştirme: API (5080) ayrı bir pencerede + Vite (5173) bu pencerede.  (Windows / PowerShell)
# llama-server'ı ayrı terminalde scripts\start-llm.ps1 ile başlatın.
# Kullanım:  powershell -ExecutionPolicy Bypass -File scripts\dev.ps1
$root = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

# API kendi penceresinde açılır; işiniz bitince o pencereyi kapatın.
Start-Process -FilePath 'dotnet' -ArgumentList 'run' -WorkingDirectory (Join-Path $root 'api')

Set-Location (Join-Path $root 'web')
if (-not (Test-Path 'node_modules')) { npm install; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
npm run dev
