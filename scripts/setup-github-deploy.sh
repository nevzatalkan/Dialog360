#!/usr/bin/env bash
# GitHub Actions → Azure deploy yetkisi (tek seferlik). Önce scripts/deploy-azure.sh ile kaynaklar kurulmuş olmalı.
# Azure Cloud Shell'de ya da az login yapılmış bir terminalde çalıştırın:
#   scripts/setup-github-deploy.sh
# Şifre/anahtar üretmez: GitHub yalnızca bu repo ve "production" ortamı için kısa ömürlü OIDC token'ıyla girer,
# yetki yalnızca Diyalog 360 web app'iyle sınırlıdır. Sonda GitHub'a girilecek 4 değeri yazdırır.
set -euo pipefail
export PYTHONWARNINGS=ignore

RG="${RG:-rg-diyalog360}"
REPO="${REPO:-nevzatalkan/Dialog360}"
ENV_NAME="${ENV_NAME:-production}"
SUFFIX="${SUFFIX:-$(az group show -n "$RG" --query tags.suffix -o tsv)}"
WEBAPP="diyalog360-api-$SUFFIX"
WEBAPP_ID=$(az webapp show -g "$RG" -n "$WEBAPP" --query id -o tsv)

APP_ID=$(az ad app list --display-name diyalog360-github --query '[0].appId' -o tsv)
[ -n "$APP_ID" ] || APP_ID=$(az ad app create --display-name diyalog360-github --query appId -o tsv)
az ad sp show --id "$APP_ID" -o none 2>/dev/null || az ad sp create --id "$APP_ID" -o none

az ad app federated-credential list --id "$APP_ID" --query "[?name=='github-$ENV_NAME']" -o tsv | grep -q . ||
  az ad app federated-credential create --id "$APP_ID" -o none --parameters "{
    \"name\": \"github-$ENV_NAME\",
    \"issuer\": \"https://token.actions.githubusercontent.com\",
    \"subject\": \"repo:$REPO:environment:$ENV_NAME\",
    \"audiences\": [\"api://AzureADTokenExchange\"]}"

# Rol ataması yeni servis sorumlusu dizine yayılana kadar birkaç kez denenir
for _ in $(seq 1 6); do
  az role assignment create --assignee "$APP_ID" --role "Website Contributor" --scope "$WEBAPP_ID" -o none 2>/dev/null && break
  sleep 10
done

cat <<OUT

✓ Tamam. GitHub → $REPO → Settings → Environments → "$ENV_NAME" ortamına şu 4 değişkeni (Variables) ekleyin:

  AZURE_CLIENT_ID        $APP_ID
  AZURE_TENANT_ID        $(az account show --query tenantId -o tsv)
  AZURE_SUBSCRIPTION_ID  $(az account show --query id -o tsv)
  AZURE_WEBAPP_NAME      $WEBAPP

Sonra Actions → "Azure'a deploy" → Run workflow (ya da main'e bir push).
OUT
