# Demo: önyüzü derleyip API'nin içinden servis eder → http://localhost:5080  (Windows / PowerShell)
# Kullanım:  powershell -ExecutionPolicy Bypass -File scripts\demo.ps1
$root = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Push-Location (Join-Path $root 'web')
try {
    if (-not (Test-Path 'node_modules')) { npm install; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
    npm run build; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally { Pop-Location }

Set-Location (Join-Path $root 'api')
dotnet run -c Release
exit $LASTEXITCODE
