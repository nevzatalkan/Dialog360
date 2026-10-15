# Dialog360 - kurulum ve calistirma (Windows / PowerShell)
$ErrorActionPreference = 'Continue'
$base = $PSScriptRoot
$root = Join-Path $base 'Dialog360-main'
$log  = Join-Path $base 'kurulum-log.txt'
Start-Transcript -Path $log -Force | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Has($cmd) { [bool](Get-Command $cmd -ErrorAction SilentlyContinue) }

Write-Host "=== ON KOSULLAR ==="
$dotnetOk = $false
if (Has 'dotnet') { $sdkList = @(dotnet --list-sdks); Write-Host "dotnet SDK'lari:`n$($sdkList -join "`n")"; $dotnetOk = [bool]($sdkList | Where-Object { $_ -match '^10\.' }) } else { Write-Host "dotnet YOK" }
if (Has 'node') { Write-Host "node $(node --version)" } else { Write-Host "node YOK" }
if (Has 'llama-server') { Write-Host "llama-server: $((Get-Command llama-server).Source)" } else { Write-Host "llama-server YOK" }
if (Has 'ollama') { Write-Host "ollama modelleri:"; ollama list } else { Write-Host "ollama YOK" }
if (Has 'nvidia-smi') { nvidia-smi --query-gpu=name,memory.total --format=csv } else { Write-Host "nvidia-smi YOK" }
Write-Host ("RAM GB: " + [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory/1GB))
foreach ($p in @('C:\models', "$HOME\models", "$HOME\.lmstudio\models", "$HOME\.cache\lm-studio\models")) {
  if (Test-Path $p) { Get-ChildItem $p -Recurse -Filter *.gguf -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "GGUF: $($_.FullName) ($([math]::Round($_.Length/1GB,1)) GB)" } }
}
foreach ($port in 5080, 18080, 11434, 1234) { if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { Write-Host "PORT $port dinleniyor" } }

$needRestart = $false
if (-not $dotnetOk) { Write-Host "=== .NET 10 SDK kuruluyor ==="; winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements; $needRestart = $true }
if (-not (Has 'node')) { Write-Host "=== Node.js LTS kuruluyor ==="; winget install --id OpenJS.NodeJS.LTS -e --accept-source-agreements --accept-package-agreements; $needRestart = $true }
if ($needRestart) {
  Write-Host "ADIM: Kurulum yapildi. PATH guncellensin diye bu pencereyi kapatip kur-ve-calistir.cmd'yi tekrar calistirin."
  Stop-Transcript | Out-Null; Read-Host "Kapatmak icin Enter"; exit 1
}

Write-Host "=== ONYUZ (npm install + build) ==="
Push-Location (Join-Path $root 'web')
npm install --no-fund --no-audit
if ($LASTEXITCODE -ne 0) { Write-Host "HATA: npm install basarisiz"; Pop-Location; Stop-Transcript | Out-Null; Read-Host "Enter"; exit 1 }
npm run build
if ($LASTEXITCODE -ne 0) { Write-Host "HATA: npm run build basarisiz"; Pop-Location; Stop-Transcript | Out-Null; Read-Host "Enter"; exit 1 }
Pop-Location

Write-Host "=== GUVENLIK TESTLERI ==="
dotnet test (Join-Path $root 'tests') --nologo
Write-Host "TESTLER cikis kodu: $LASTEXITCODE"

Write-Host "=== LM STUDIO MODELI (Qwen3 Coder 30B) ==="
$lms = (Get-Command lms -ErrorAction SilentlyContinue).Source
if (-not $lms) { $c = Join-Path $HOME '.lmstudio\bin\lms.exe'; if (Test-Path $c) { $lms = $c } }
if (-not $lms) {
  Write-Host "UYARI: lms komutu bulunamadi. LM Studio'yu acip Developer sekmesinden sunucuyu baslatin ve Qwen3 Coder 30B'yi yukleyin."
} else {
  Write-Host "lms: $lms"
  & $lms server start
  $list = (& $lms ls) -join "`n"
  Write-Host $list
  $key = ([regex]::Matches($list, '(?im)^\s*(\S*qwen3-coder-30b\S*)') | Select-Object -First 1).Groups[1].Value
  if (-not $key) { $key = 'qwen/qwen3-coder-30b' }
  Write-Host "Yuklenecek model: $key"
  & $lms unload --all
  & $lms load $key --context-length 16384 -y
  & $lms ps
  try {
    $ids = (Invoke-RestMethod http://127.0.0.1:1234/v1/models -TimeoutSec 10).data.id
    Write-Host "Sunucudaki modeller: $($ids -join ', ')"
    $match = $ids | Where-Object { $_ -match 'qwen3-coder-30b' } | Select-Object -First 1
    if ($match) { $env:Llm__Model = $match; Write-Host "API modeli: $match" }
  } catch { Write-Host "UYARI: LM Studio sunucusuna ulasilamadi: $_" }
}

Write-Host "=== API BASLIYOR -> http://localhost:5080 (kapatmak icin bu pencereyi kapatin) ==="
Set-Location (Join-Path $root 'api')
dotnet run -c Release
Stop-Transcript | Out-Null
