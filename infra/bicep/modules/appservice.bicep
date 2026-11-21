param planName string
param apiAppName string
param workerAppName string
param location string
param tags object

@allowed([
  'S1'
  'S2'
  'P0v3'
  'P1v3'
])
@description('Standard or higher is required for slots; set per environment in the .bicepparam.')
param planSkuName string = 'S1'

@description('Service Bus namespace resource ID used by plan autoscale ActiveMessages rules.')
param serviceBusId string = ''

@description('Value for ServiceBus__FullyQualifiedNamespace (output of the servicebus module).')
param serviceBusFqdn string

@description('Value for BlobStorage__ServiceUri (output of the storage module).')
param blobServiceUri string
param apiBlobContainerName string

@description('Azure SQL server FQDN. Empty = no SQL connection settings are written.')
param sqlServerFqdn string = ''
param sqlDatabaseName string = ''

param apiHealthCheckPath string = '/health'
param workerHealthCheckPath string = '/health'

param azureAdTenantId string = ''
param azureAdClientId string = ''
param appInsightsName string

// ---------- AI / RAG settings (Week 2, Day 2) ----------
param aiEnabled bool = false
param openAiEndpoint string = ''
param openAiChatModel string = 'gpt-5-mini'
param openAiEmbeddingModel string = 'text-embedding-3-small'
param openAiEmbeddingDimensions int = 1536
param aiSearchEndpoint string = ''
param aiSearchIndexName string = 'azurelab-docs'
param aiSearchMode string = 'HybridSemantic'
param contentSafetyEnabled bool = false
param contentSafetyEndpoint string = ''

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

var linuxFxVersion = 'DOTNETCORE|10.0'

// ---------- App settings: keys exactly as the code reads them ----------
// No secrets. ConnectionStrings__Default is passwordless: the app's system-assigned managed identity gets the token.
var apiBaseSettings = {
  ServiceBus__FullyQualifiedNamespace: serviceBusFqdn
  ServiceBus__RedeemQueue: 'redeem-requests'
  ServiceBus__OfferTopic: 'offer-events'
  BlobStorage__ServiceUri: blobServiceUri
  BlobStorage__ContainerName: apiBlobContainerName
  BlobStorage__CreateContainerIfMissing: 'false'
  BlobStorage__AllowHttpSas: 'false'
  WEBSITE_RUN_FROM_PACKAGE: '1'
}

var aiSettings = aiEnabled && !empty(openAiEndpoint) && !empty(aiSearchEndpoint)
  ? {
      Ai__Enabled: 'true'
      Ai__Provider: 'AzureOpenAI'
      Ai__AzureOpenAI__Endpoint: openAiEndpoint
      Ai__Chat__Model: openAiChatModel
      Ai__Chat__TimeoutSeconds: '60'
      Ai__Embedding__Model: openAiEmbeddingModel
      Ai__Embedding__Dimensions: string(openAiEmbeddingDimensions)
      Ai__Embedding__TimeoutSeconds: '30'
      Ai__Embedding__DocumentPrefix: ''
      Ai__Embedding__QueryPrefix: ''
      Ai__VectorStore__Provider: 'AzureAISearch'
      Ai__VectorStore__AzureAISearch__Endpoint: aiSearchEndpoint
      Ai__VectorStore__AzureAISearch__IndexName: aiSearchIndexName
      Ai__VectorStore__AzureAISearch__SearchMode: aiSearchMode
      Ai__VectorStore__AzureAISearch__SemanticConfigurationName: 'sem-default'
      Ai__Query__MaxAnswerTokens: '900'
      Ai__Query__MinSimilarity: '0.45'
    }
  : {
      Ai__Enabled: 'false'
    }

var contentSafetySettings = aiEnabled && contentSafetyEnabled && !empty(contentSafetyEndpoint)
  ? {
      Ai__ContentSafety__Enabled: 'true'
      Ai__ContentSafety__Endpoint: contentSafetyEndpoint
      Ai__ContentSafety__TimeoutSeconds: '10'
      Ai__ContentSafety__SeverityThreshold: '2'
      Ai__ContentSafety__FailClosedOnInput: 'true'
      Ai__ContentSafety__FailClosedOnDocuments: 'true'
      Ai__ContentSafety__FailClosedOnOutput: 'false'
    }
  : {
      Ai__ContentSafety__Enabled: 'false'
    }

// Passwordless: no User Id, no Password. Connection Timeout=60 covers a serverless auto-resume (about 1 minute).
var sqlConnection = 'Server=tcp:${sqlServerFqdn},1433;Database=${sqlDatabaseName};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60'
var sqlSettings = empty(sqlServerFqdn) ? {} : { ConnectionStrings__Default: sqlConnection }
var authSettings = empty(azureAdTenantId) ? {} : {
  AzureAd__Instance: environment().authentication.loginEndpoint
  AzureAd__TenantId: azureAdTenantId
  AzureAd__ClientId: azureAdClientId
}
var appInsightsSettings = {
  APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
}
var apiSettings = union(apiBaseSettings, aiSettings, contentSafetySettings, sqlSettings, authSettings, appInsightsSettings)

// Values mirror AzureLab.Worker/appsettings.json. Redeem has no Subscription (queue).
// Outbox__Destination is deliberately NOT set: OutboxOptions binds "Destinations" (backlog).
var workerBaseSettings = {
  ServiceBus__FullyQualifiedNamespace: serviceBusFqdn
  Consumers__Redeem__EntityPath: 'redeem-requests'
  Consumers__Redeem__MaxConcurrentCalls: '1'
  Consumers__Redeem__UnknownMessagePolicy: 'DeadLetter'
  Consumers__Ledger__EntityPath: 'offer-events'
  Consumers__Ledger__Subscription: 'ledger'
  Consumers__Ledger__MaxConcurrentCalls: '1'
  Consumers__Ledger__UnknownMessagePolicy: 'Complete'
  Consumers__Notification__EntityPath: 'offer-events'
  Consumers__Notification__Subscription: 'notification'
  Consumers__Notification__MaxConcurrentCalls: '4'
  Consumers__Notification__UnknownMessagePolicy: 'Complete'
  Outbox__BatchSize: '20'
  Outbox__MaxAttempts: '5'
  WEBSITE_RUN_FROM_PACKAGE: '1'
}
var workerSettings = union(workerBaseSettings, sqlSettings, appInsightsSettings)

// ---------- Plan ----------
resource appPlan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: planSkuName
  }
  properties: {
    reserved: true // Linux
  }
}

resource planAutoscale 'Microsoft.Insights/autoscalesettings@2022-10-01' = if (!empty(serviceBusId)) {
  name: 'autoscale-${planName}'
  location: location
  tags: tags
  properties: {
    name: 'autoscale-${planName}'
    enabled: true
    targetResourceUri: appPlan.id
    profiles: [
      {
        name: 'DefaultProfile'
        capacity: {
          minimum: '1'
          maximum: '2'
          default: '1'
        }
        rules: [
          {
            metricTrigger: {
              metricName: 'CpuPercentage'
              metricNamespace: 'Microsoft.Web/serverfarms'
              metricResourceUri: appPlan.id
              timeGrain: 'PT1M'
              statistic: 'Average'
              timeWindow: 'PT5M'
              timeAggregation: 'Average'
              operator: 'GreaterThan'
              threshold: 60
            }
            scaleAction: {
              direction: 'Increase'
              type: 'ChangeCount'
              value: '1'
              cooldown: 'PT5M'
            }
          }
          {
            metricTrigger: {
              metricName: 'ActiveMessages'
              metricNamespace: 'Microsoft.ServiceBus/namespaces'
              metricResourceUri: serviceBusId
              timeGrain: 'PT1M'
              statistic: 'Average'
              timeWindow: 'PT5M'
              timeAggregation: 'Average'
              operator: 'GreaterThan'
              threshold: 50
            }
            scaleAction: {
              direction: 'Increase'
              type: 'ChangeCount'
              value: '1'
              cooldown: 'PT5M'
            }
          }
          {
            metricTrigger: {
              metricName: 'CpuPercentage'
              metricNamespace: 'Microsoft.Web/serverfarms'
              metricResourceUri: appPlan.id
              timeGrain: 'PT1M'
              statistic: 'Average'
              timeWindow: 'PT10M'
              timeAggregation: 'Average'
              operator: 'LessThan'
              threshold: 25
            }
            scaleAction: {
              direction: 'Decrease'
              type: 'ChangeCount'
              value: '1'
              cooldown: 'PT10M'
            }
          }
          {
            metricTrigger: {
              metricName: 'ActiveMessages'
              metricNamespace: 'Microsoft.ServiceBus/namespaces'
              metricResourceUri: serviceBusId
              timeGrain: 'PT1M'
              statistic: 'Average'
              timeWindow: 'PT10M'
              timeAggregation: 'Average'
              operator: 'LessThan'
              threshold: 10
            }
            scaleAction: {
              direction: 'Decrease'
              type: 'ChangeCount'
              value: '1'
              cooldown: 'PT10M'
            }
          }
        ]
      }
    ]
  }
}

// ---------- API ----------
resource apiApp 'Microsoft.Web/sites@2024-11-01' = {
  name: apiAppName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appPlan.id
    httpsOnly: true
    clientAffinityEnabled: false // stateless API: no ARR affinity cookie
    siteConfig: {
      linuxFxVersion: linuxFxVersion
      alwaysOn: true
      healthCheckPath: apiHealthCheckPath
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
    }
  }
}

resource apiAppSettings 'Microsoft.Web/sites/config@2024-11-01' = {
  parent: apiApp
  name: 'appsettings'
  properties: union(apiSettings, {
    ASPNETCORE_ENVIRONMENT: 'Production'
  })
}

// Sticky (slot) settings: stay with the slot on swap. Only "where it runs", never "what the code needs".
resource apiSlotConfig 'Microsoft.Web/sites/config@2024-11-01' = {
  parent: apiApp
  name: 'slotConfigNames'
  properties: {
    appSettingNames: [
      'ASPNETCORE_ENVIRONMENT'
      'WEBSITE_SWAP_WARMUP_PING_PATH'
      'WEBSITE_SWAP_WARMUP_PING_STATUSES'
    ]
  }
}

resource apiStaging 'Microsoft.Web/sites/slots@2024-11-01' = {
  parent: apiApp
  name: 'staging'
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned' // its OWN principal: needs its own roles in Step 7
  }
  properties: {
    serverFarmId: appPlan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: linuxFxVersion
      alwaysOn: true
      healthCheckPath: apiHealthCheckPath
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
    }
  }
}

// Slots do NOT inherit app settings: define them explicitly.
resource apiStagingSettings 'Microsoft.Web/sites/slots/config@2024-11-01' = {
  parent: apiStaging
  name: 'appsettings'
  properties: union(apiSettings, {
    ASPNETCORE_ENVIRONMENT: 'Staging'
    WEBSITE_SWAP_WARMUP_PING_PATH: apiHealthCheckPath
    WEBSITE_SWAP_WARMUP_PING_STATUSES: '200'
  })
}

// ---------- Worker (separate web app, same plan, NO staging slot: see Decision 1) ----------
resource workerApp 'Microsoft.Web/sites@2024-11-01' = {
  name: workerAppName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appPlan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: linuxFxVersion
      alwaysOn: true // without it the app is unloaded when idle and the consumers stop
      healthCheckPath: workerHealthCheckPath
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
    }
  }
}

resource workerAppSettings 'Microsoft.Web/sites/config@2024-11-01' = {
  parent: workerApp
  name: 'appsettings'
  properties: workerSettings
}

output apiPrincipalId string = apiApp.identity.principalId
output apiStagingPrincipalId string = apiStaging.identity.principalId
output workerPrincipalId string = workerApp.identity.principalId
output apiDefaultHostName string = apiApp.properties.defaultHostName
output workerDefaultHostName string = workerApp.properties.defaultHostName
output planId string = appPlan.id
