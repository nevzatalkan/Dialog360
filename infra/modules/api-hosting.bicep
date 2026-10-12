targetScope = 'resourceGroup'

@description('Azure region for application hosting resources.')
param location string

@description('Tags applied to all resources.')
param tags object = {}

@description('Foundry Container Registry login server.')
param acrEndpoint string

@description('Foundry account name.')
param foundryAccountName string

@description('Foundry project name.')
param foundryProjectName string

@description('Foundry model deployment name.')
param modelDeploymentName string

@description('Hosted agent name.')
param agentName string

var suffix = uniqueString(resourceGroup().id)
var acrName = first(split(acrEndpoint, '.'))
var foundryProjectEndpoint = 'https://${foundryAccountName}.services.ai.azure.com/api/projects/${foundryProjectName}'
var agentEndpoint = '${foundryProjectEndpoint}/agents/${agentName}/endpoint/protocols/openai/responses'
var agentConsumerRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'eed3b665-ab3a-47b6-8f48-c9382fb1dad6'
)
var acrPullRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d'
)

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: 'd360-law-${suffix}'
  location: location
  tags: tags
  properties: {
    retentionInDays: 30
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource managedEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'd360-env-${suffix}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: acrName
}

resource foundryAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: foundryAccountName
}

resource foundryProject 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' existing = {
  parent: foundryAccount
  name: foundryProjectName
}

resource apiApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'd360-api-${suffix}'
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: managedEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 5080
        transport: 'auto'
        allowInsecure: false
      }
      registries: [
        {
          server: acrEndpoint
          identity: 'system'
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'dialog360-api'
          image: '${acrEndpoint}/dialog360-api:latest'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'Llm__Provider'
              value: 'FoundryAgent'
            }
            {
              name: 'Llm__AgentEndpoint'
              value: agentEndpoint
            }
            {
              name: 'Llm__Model'
              value: modelDeploymentName
            }
            {
              name: 'Llm__UseManagedIdentity'
              value: 'true'
            }
          ]
          probes: [
            {
              type: 'Readiness'
              httpGet: {
                path: '/api/health'
                port: 5080
              }
              initialDelaySeconds: 5
              periodSeconds: 10
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/api/health'
                port: 5080
              }
              initialDelaySeconds: 15
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 2
      }
    }
  }
}

resource apiAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, apiApp.name, acrPullRoleId)
  scope: registry
  properties: {
    principalId: apiApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: acrPullRoleId
  }
}

resource apiFoundryAgentConsumer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryProject.id, apiApp.name, agentConsumerRoleId)
  scope: foundryProject
  properties: {
    principalId: apiApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: agentConsumerRoleId
  }
}

output AZURE_CONTAINER_APP_NAME string = apiApp.name
output AZURE_CONTAINER_APP_RESOURCE_ID string = apiApp.id
output DIALOG360_API_URL string = 'https://${apiApp.properties.configuration.ingress.fqdn}'
