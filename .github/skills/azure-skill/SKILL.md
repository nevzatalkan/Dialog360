---
name: azure-skill
description: "Use when preparing, validating, deploying, or troubleshooting Dialog360 on Microsoft Foundry and Azure, including its gpt-5-mini hosted agent, Container Apps API, ACR remote builds, Bicep infrastructure, azd, quotas, and managed identity."
---

# Dialog360 on Microsoft Foundry

Use this skill for Azure and Microsoft Foundry work in the Dialog360 repository. Follow the Azure `azure-prepare` → `azure-validate` → `azure-deploy` lifecycle; do not skip validation or report a deployment as complete until its endpoints have been checked.

## Current architecture

- The React UI calls the existing `/api/*` endpoints. Do not expose Foundry credentials or endpoints to the browser.
- The ASP.NET Core API calls the Foundry Responses endpoint through the C# hosted agent.
- The hosted agent returns structured report requests. It has no database connection and does not execute SQL.
- Treat all generated SQL as untrusted. The API must keep routing it through the existing `SqlGuard` and `QueryRunner` safeguards before execution.
- The Azure model is `gpt-5-mini`, catalog version `2025-08-07`, on `GlobalStandard`. The deployment capacity is configured as 10; check live capacity again before provisioning.
- The API and bundled React UI run in Azure Container Apps. The API uses a system-assigned identity to call the agent and pull its image from ACR.
- The API selects its model backend with `Llm:Provider`: `FoundryAgent` in Azure, while the local `OpenAICompatible` default preserves the existing Ollama-compatible development workflow. Do not expose the agent endpoint or credentials to the browser.
- The API health endpoint is `/api/health`; it reports model provider/health and schema table count. Its readiness and liveness probes use this endpoint on port 5080.
- The hosted agent uses the .NET 10 code-deployment configuration. Keep its project and Docker build contexts separate from the API's repository-root context.
- The API Dockerfile builds the React assets and ASP.NET Core API into one image; the API listens on port 5080 and Container Apps ingress must target the same port.
- Chat, cache, audit, and seeded SQLite files are local to the container unless a persistent store is added; do not promise persistence across container replacement.

## Configuration locations

- `azure.yaml`: AZD services, model deployment, hosted-agent runtime, and remote API image build.
- `infra/`: Microsoft Foundry, model deployment, Container Apps, ACR, Log Analytics, and role assignments.
- `src/d360-sql-agent/`: C# hosted-agent source and its Dockerfile.
- `api/`: browser-facing API, Foundry client integration, and SQL safety boundary.
- `web/`: React client.
- `README.md`: local Foundry-agent setup and Azure deployment notes.
- `.azure/deployment-plan.md`: local, ignored planning/validation/deployment state; never commit its per-environment values.

## Preflight and validation

1. Use the subscription and location already approved for the current task. Confirm them with `az account show` and `azd env get-values`; do not silently switch either.
2. Ensure the AZD environment contains `AZURE_SUBSCRIPTION_ID`, `AZURE_LOCATION`, `AZURE_RESOURCE_GROUP`, and `AZURE_AI_MODEL_DEPLOYMENT_NAME`.
3. Check that the target resource group does not already exist or contain conflicting `azd-service-name` resources before provisioning.
4. Check current model availability and capacity:

   ```powershell
   az cognitiveservices model list --location northcentralus
   az cognitiveservices usage list --location northcentralus
   ```

   `az quota list` may return `BadRequest` for `Microsoft.CognitiveServices`; when that happens, use the Cognitive Services model and usage commands instead. Confirm that the configured SKU, model version, feature support, and capacity remain available.
5. Run `azd provision --preview --no-prompt` and resolve all infrastructure or policy errors before creating resources.
6. Build and test the API, agent, and UI. Verify the root API Dockerfile's `web/package-lock.json` input and confirm its listening port matches the Container Apps ingress target port.
7. Run `azd package --no-prompt`. The API uses ACR remote build when ACR Tasks are enabled for the subscription. Keep the API service configured as a Docker language service with its source `project` and Dockerfile; do not set `image` as well, because `image` means an already-built source image and conflicts with `project`.
8. Inspect Bicep role assignments: the API identity needs `AcrPull` on the registry and `Foundry Agent Consumer` on the Foundry project. The Foundry project identity needs `AcrPull` only when the agent image path requires it. Keep scopes least-privilege.
9. Complete `azure-validate` and ensure `.azure/deployment-plan.md` has status `Validated` and actual validation evidence before deployment.

## Provision and deploy

Set the Foundry skill user-agent only for the current AZD process; never persist it in source, `azure.yaml`, or AZD environment values:

```powershell
$env:AZURE_DEV_USER_AGENT = "microsoft_foundry_skill"
azd provision --no-prompt
```

Provision and deploy as separate operations when Container Apps uses ACR. After provision:

1. Read the new AZD outputs with `azd env get-values`.
2. Confirm the Container App system identity has `AcrPull` on ACR and allow role propagation before deploying its revision.
3. Deploy the hosted agent and API with `azd deploy --no-prompt`.
4. Run the Foundry agent doctor after the project exists; an unset project endpoint is expected before provisioning, not after.
5. Verify the hosted agent is active and make one successful Responses-protocol smoke invocation.
6. Obtain service URLs from `azd show`. Test the API health endpoint and the public UI over HTTPS, and inspect Container Apps logs if either fails.
7. Verify live role assignments against the expected identities/scopes and record results in the local deployment plan.
8. Report the fully qualified HTTPS endpoints and the resource group portal link. Do not claim success if any required health check or agent invocation fails.

## Operational constraints

- Before the first API Container App revision, ensure the image tag exists in ACR and the app identity's `AcrPull` assignment is effective. Use a bootstrap image/path if infrastructure must create the identity and role assignment before deploying the private API image; do not repeatedly provision against a missing private tag.
- If ACR remote build returns `TasksOperationsNotAllowed`, stop retrying the same build command. Ask the subscription/registry administrator to enable ACR Tasks, or install Docker/Podman and switch the API service to a local build before packaging and pushing the image.
- `AZURE_DEV_USER_AGENT=microsoft_foundry_skill` is local-only and must be set inline/in-process for AZD commands, never added to committed files.
- Do not place secrets, API keys, or user-specific identity IDs in source or this skill.
- Do not weaken SQL validation, expose the agent endpoint to the browser, or grant broad project/resource-group permissions to the API identity.
- Treat Azure resources as billable. Provision only the approved environment and avoid creating duplicate resource groups or environments.
- Keep model capacity and region availability dynamic; the values in this skill describe project configuration, not a guarantee of current service availability.
