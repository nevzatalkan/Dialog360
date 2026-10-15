#!/usr/bin/env bash
# Diyalog 360 → Azure: Microsoft Foundry (kaynak + proje, gpt-4.1-mini) + tek App Service (backend + önyüz aynı adreste) + Key Vault.
# Ön koşul: az login, dotnet 10, node. İlk çalıştırmada kaynakları oluşturur, sonrakilerde yalnızca kodu yeniden deploy eder.
#
#   scripts/deploy-azure.sh            → oluştur/güncelle + deploy
#   SUFFIX=fe3b1e scripts/deploy-azure.sh  → mevcut kaynaklara deploy (ad soneki)
#
# Giriş kullanıcısı: ilk çalıştırmada kullanıcı adı/şifre sorulur; yalnızca PBKDF2 özeti Key Vault'a gider.
# Gizli değerler (OpenAI anahtarı, giriş özeti) Key Vault'ta; App Service onları yönetilen kimlikle okur.
# Ücret: App Service B1 (~13 USD/ay) + Foundry model kullanımı (token başına). Kaldırmak için: az group delete -n rg-diyalog360
set -euo pipefail
cd "$(dirname "$0")/.."
export PYTHONWARNINGS=ignore DOTNET_CLI_TELEMETRY_OPTOUT=1

RG="${RG:-rg-diyalog360}"
LOC="${LOC:-swedencentral}"
SUFFIX="${SUFFIX:-$(az group show -n "$RG" --query tags.suffix -o tsv 2>/dev/null || true)}"
SUFFIX="${SUFFIX:-$(openssl rand -hex 3)}"
FOUNDRY="foundry-diyalog360-$SUFFIX"
PROJECT="${PROJECT:-diyalog360}"
API="diyalog360-api-$SUFFIX"
PLAN="asp-diyalog360"
KV="kv-diyalog360-$SUFFIX"
MODEL="${MODEL:-gpt-4.1-mini}"
MODEL_VERSION="${MODEL_VERSION:-2025-04-14}"
CAPACITY="${CAPACITY:-200}"   # bin token/dk; GlobalStandard'da ücret kullanılan token'a göredir

step() { printf '\n▶ %s\n' "$*"; }

step "Sağlayıcılar ve kaynak grubu ($RG, $LOC, sonek $SUFFIX)"
az provider register -n Microsoft.Web --wait >/dev/null
az provider register -n Microsoft.CognitiveServices --wait >/dev/null
az provider register -n Microsoft.KeyVault --wait >/dev/null
az group create -n "$RG" -l "$LOC" --tags suffix="$SUFFIX" -o none

step "Microsoft Foundry kaynağı + proje + $MODEL deployment"
az cognitiveservices account show -n "$FOUNDRY" -g "$RG" -o none 2>/dev/null ||
  az cognitiveservices account create -n "$FOUNDRY" -g "$RG" -l "$LOC" --kind AIServices --sku S0 --custom-domain "$FOUNDRY" \
    --allow-project-management --yes -o none
az cognitiveservices account project show -n "$FOUNDRY" -g "$RG" --project-name "$PROJECT" -o none 2>/dev/null ||
  az cognitiveservices account project create -n "$FOUNDRY" -g "$RG" --project-name "$PROJECT" -l "$LOC" -o none
az cognitiveservices account deployment create -g "$RG" -n "$FOUNDRY" --deployment-name "$MODEL" --model-name "$MODEL" \
  --model-version "$MODEL_VERSION" --model-format OpenAI --sku-name GlobalStandard --sku-capacity "$CAPACITY" -o none

step "App Service (Linux B1, .NET 10)"
az appservice plan show -n "$PLAN" -g "$RG" -o none 2>/dev/null || az appservice plan create -n "$PLAN" -g "$RG" -l "$LOC" --sku B1 --is-linux -o none
az webapp show -n "$API" -g "$RG" -o none 2>/dev/null || az webapp create -n "$API" -g "$RG" -p "$PLAN" --runtime "DOTNETCORE:10.0" --https-only true -o none
az webapp config set -g "$RG" -n "$API" --always-on true --http20-enabled true --min-tls-version 1.2 --ftps-state Disabled -o none

step "Key Vault: model anahtarı ve giriş özeti secret olarak; uygulama yönetilen kimlikle okur"
az keyvault show -n "$KV" -g "$RG" -o none 2>/dev/null ||
  az keyvault create -n "$KV" -g "$RG" -l "$LOC" --enable-rbac-authorization true --retention-days 7 -o none
KVID=$(az keyvault show -n "$KV" -g "$RG" --query id -o tsv)
ME=$(az ad signed-in-user show --query id -o tsv)
az role assignment create --assignee-object-id "$ME" --assignee-principal-type User --role "Key Vault Secrets Officer" --scope "$KVID" -o none 2>/dev/null || true
APP_MI=$(az webapp identity assign -g "$RG" -n "$API" --query principalId -o tsv)
az role assignment create --assignee-object-id "$APP_MI" --assignee-principal-type ServicePrincipal --role "Key Vault Secrets User" --scope "$KVID" -o none 2>/dev/null || true

SECRET=$(mktemp); chmod 600 "$SECRET"; trap 'rm -f "$SECRET"' EXIT
put_secret() {  # rol ataması yayılana kadar yeniden dener; değer komut satırında görünmez
  for _ in $(seq 1 12); do az keyvault secret set --vault-name "$KV" -n "$1" --file "$SECRET" --encoding utf-8 -o none 2>/dev/null && return 0; sleep 10; done
  echo "HATA: $1 Key Vault'a yazılamadı" >&2; return 1
}
az cognitiveservices account keys list -g "$RG" -n "$FOUNDRY" --query key1 -o tsv | tr -d '\n' > "$SECRET"; put_secret llm-api-key
if ! az keyvault secret show --vault-name "$KV" -n auth-credentials -o none 2>/dev/null; then
  echo "Buluttaki uygulama için giriş kullanıcısı oluşturun (yalnızca PBKDF2 özeti Key Vault'a gider):"
  (cd api && dotnet run -- set-password --print | tail -1 | tr -d '\n') > "$SECRET"; put_secret auth-credentials
fi
: > "$SECRET"

step "Uygulama ayarları (gizli değerler yalnızca Key Vault referansı olarak)"
az webapp config appsettings set -g "$RG" -n "$API" -o none --settings \
  "Llm__BaseUrl=https://$FOUNDRY.services.ai.azure.com/openai/v1" "Llm__Model=$MODEL" "Llm__LlamaCachePrompt=false" \
  "Llm__ApiKey=@Microsoft.KeyVault(VaultName=$KV;SecretName=llm-api-key)" \
  "Auth__Credentials=@Microsoft.KeyVault(VaultName=$KV;SecretName=auth-credentials)" \
  "Speech__Provider=None" "Storage__DataDir=/home/data" "Storage__Wal=false" \
  "ASPNETCORE_FORWARDEDHEADERS_ENABLED=true" "Urls=http://+:8080" "WEBSITES_PORT=8080" "WEBSITES_ENABLE_APP_SERVICE_STORAGE=true"

step "Derle ve paketle (önyüz api/wwwroot'a; Data/ pakete girmez)"
(cd web && { [ -d node_modules ] || npm ci; } && npm run build)
OUT=$(mktemp -d); (cd api && dotnet publish -c Release -o "$OUT" >/dev/null)
if find "$OUT" \( -name auth.json -o -name '*.db' -o -path '*keys*' \) | grep -q .; then echo "HATA: pakette hassas dosya var" >&2; exit 1; fi
ZIP=$(mktemp -u).zip; (cd "$OUT" && zip -qr "$ZIP" .)

step "Deploy"
az webapp deploy -g "$RG" -n "$API" --src-path "$ZIP" --type zip --async false -o none
rm -rf "$OUT" "$ZIP"
echo; echo "✓ Hazır: https://$API.azurewebsites.net"
