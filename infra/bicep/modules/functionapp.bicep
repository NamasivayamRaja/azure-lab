param name string
param location string
param tags object
param planId string
param storageAccountName string
param serviceBusFqdn string
param blobServiceUri string
param appInsightsName string

@description('Azure SQL server FQDN. Empty = no SQL connection setting is written.')
param sqlServerFqdn string = ''
param sqlDatabaseName string = ''

param healthCheckPath string = '/api/health'

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

resource functionApp 'Microsoft.Web/sites@2024-11-01' = {
  name: name
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: planId
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|10.0'
      alwaysOn: true // the polling blob trigger needs the host running
      healthCheckPath: healthCheckPath
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
    }
  }
}

// Keys exactly as the Functions code and host read them. No keys, no connection strings for storage or Service Bus.
// TODO (cloud week 1, Azure SQL): ConnectionStrings__LabDb.
// ConnectionStrings__LabDb is passwordless (system-assigned managed identity). Connection Timeout=60 covers a serverless auto-resume.
var sqlSettings = empty(sqlServerFqdn) ? {} : { ConnectionStrings__LabDb: 'Server=tcp:${sqlServerFqdn},1433;Database=${sqlDatabaseName};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60' }

var baseFunctionSettings = union({
  FUNCTIONS_EXTENSION_VERSION: '~4'
  FUNCTIONS_WORKER_RUNTIME: 'dotnet-isolated'
  AzureWebJobsStorage__accountName: storageAccountName // identity-based host storage (blob trigger)
  AzureWebJobsStorage__blobServiceUri: blobServiceUri
  AzureWebJobsStorage__queueServiceUri: replace(blobServiceUri, '.blob.', '.queue.')
  ServiceBusConnection__fullyQualifiedNamespace: serviceBusFqdn
  EventDocsStorage__blobServiceUri: blobServiceUri
  APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
  Ai__Enabled: 'false' // explicit: AI stays off in cloud week 1 (code default is also false)
  WEBSITE_RUN_FROM_PACKAGE: '1'
}, sqlSettings)

resource functionSettings 'Microsoft.Web/sites/config@2024-11-01' = {
  parent: functionApp
  name: 'appsettings'
  properties: union(baseFunctionSettings, {
    'AzureWebJobs.EventDocumentFunction.Disabled': 'false'
    'AzureWebJobs.OfferNotificationFunction.Disabled': 'false'
    'AzureWebJobs.DocumentIngestionFunction.Disabled': 'false'
  })
}

// Sticky slot settings: keep triggers disabled in staging and enabled in production across swaps
resource functionSlotConfig 'Microsoft.Web/sites/config@2024-11-01' = {
  parent: functionApp
  name: 'slotConfigNames'
  properties: {
    appSettingNames: [
      'WEBSITE_SWAP_WARMUP_PING_PATH'
      'WEBSITE_SWAP_WARMUP_PING_STATUSES'
      'AzureWebJobs.EventDocumentFunction.Disabled'
      'AzureWebJobs.OfferNotificationFunction.Disabled'
      'AzureWebJobs.DocumentIngestionFunction.Disabled'
    ]
  }
}

resource functionStaging 'Microsoft.Web/sites/slots@2024-11-01' = {
  parent: functionApp
  name: 'staging'
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: planId
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|10.0'
      alwaysOn: true
      healthCheckPath: healthCheckPath
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
    }
  }
}

resource functionStagingSettings 'Microsoft.Web/sites/slots/config@2024-11-01' = {
  parent: functionStaging
  name: 'appsettings'
  properties: union(baseFunctionSettings, {
    WEBSITE_SWAP_WARMUP_PING_PATH: healthCheckPath
    WEBSITE_SWAP_WARMUP_PING_STATUSES: '200'
    'AzureWebJobs.EventDocumentFunction.Disabled': 'true'
    'AzureWebJobs.OfferNotificationFunction.Disabled': 'true'
    'AzureWebJobs.DocumentIngestionFunction.Disabled': 'true'
  })
}

output principalId string = functionApp.identity.principalId
output stagingPrincipalId string = functionStaging.identity.principalId
output defaultHostName string = functionApp.properties.defaultHostName
