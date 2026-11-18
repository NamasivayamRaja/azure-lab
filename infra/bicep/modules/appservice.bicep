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
  Ai__Enabled: 'false' // explicit: AI stays off in cloud week 1 (code default is also false)
  WEBSITE_RUN_FROM_PACKAGE: '1'
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
var apiSettings = union(apiBaseSettings, sqlSettings, authSettings, appInsightsSettings)

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
