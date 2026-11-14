param logAnalyticsName string
param appInsightsName string
param location string
param tags object

@minValue(30)
@maxValue(730)
param retentionDays int = 30

@description('Daily ingestion cap in GB. -1 = no cap. Cost guard for labs.')
param dailyQuotaGb int = 1

resource workspace 'Microsoft.OperationalInsights/workspaces@2026-03-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionDays
    workspaceCapping: {
      dailyQuotaGb: dailyQuotaGb
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id // workspace-based: telemetry lands in Log Analytics
    IngestionMode: 'LogAnalytics'
    // Conscious trade-off: the connection string is an ingestion identifier, not a data-read key.
    // TODO cloud week 1: switch to Entra-authenticated telemetry (DisableLocalAuth + Monitoring Metrics Publisher role).
    DisableLocalAuth: false
  }
}

output workspaceId string = workspace.id
output appInsightsName string = appInsights.name
