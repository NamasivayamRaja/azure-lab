param workspaceId string
param storageAccountName string
param serviceBusName string
param apiAppName string
param workerAppName string
param functionAppName string

resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' existing = {
  name: storageAccountName
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-06-01' existing = {
  parent: storage
  name: 'default'
}

resource sbNamespace 'Microsoft.ServiceBus/namespaces@2026-01-01' existing = {
  name: serviceBusName
}

resource apiApp 'Microsoft.Web/sites@2024-11-01' existing = {
  name: apiAppName
}

resource workerApp 'Microsoft.Web/sites@2024-11-01' existing = {
  name: workerAppName
}

resource functionApp 'Microsoft.Web/sites@2024-11-01' existing = {
  name: functionAppName
}

// categoryGroup 'allLogs' = every log category the resource supports (no category-name guessing).
#disable-next-line use-recent-api-versions
resource blobDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: blobService
  properties: {
    workspaceId: workspaceId
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'Transaction'
        enabled: true
      }
    ]
  }
}
#disable-next-line use-recent-api-versions
resource sbDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: sbNamespace
  properties: {
    workspaceId: workspaceId
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}
#disable-next-line use-recent-api-versions
resource apiDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: apiApp
  properties: {
    workspaceId: workspaceId
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}
#disable-next-line use-recent-api-versions
resource workerDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: workerApp
  properties: {
    workspaceId: workspaceId
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}
#disable-next-line use-recent-api-versions
resource functionDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: functionApp
  properties: {
    workspaceId: workspaceId
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}
